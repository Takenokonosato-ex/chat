using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace chat;

public sealed class SocketMessageChannel : IDisposable
{
    private const uint MaxMessageBytes = 64 * 1024;
    private const uint MaxPublicKeyBytes = 4096;

    private readonly StreamSocket _socket;
    private readonly DataReader _reader;
    private readonly DataWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CryptoSession? _cryptoSession;
    private bool _disposed;

    public SocketMessageChannel(StreamSocket socket)
    {
        _socket = socket;
        _reader = new DataReader(socket.InputStream)
        {
            ByteOrder = ByteOrder.LittleEndian,
            UnicodeEncoding = UnicodeEncoding.Utf8
        };
        _writer = new DataWriter(socket.OutputStream)
        {
            ByteOrder = ByteOrder.LittleEndian,
            UnicodeEncoding = UnicodeEncoding.Utf8
        };
    }

    public bool IsSecure => _cryptoSession is not null;

    public async Task<Guid> HandshakeAsync(Guid localSessionId)
    {
        using var localKey = EcdhService.Create();
        var publicKey = EcdhService.GetPublicKey(localKey);

        // 自分のセッションIDとECDH公開鍵を送ります。
        await _writeLock.WaitAsync();
        try
        {
            _writer.WriteBytes(localSessionId.ToByteArray());
            _writer.WriteUInt32((uint)publicKey.Length);
            _writer.WriteBytes(publicKey);
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        // 相手のセッションIDとECDH公開鍵を受け取ります。
        if (!await LoadExactAsync(16))
        {
            return Guid.Empty;
        }

        var guidBytes = new byte[16];
        _reader.ReadBytes(guidBytes);
        var remoteSessionId = new Guid(guidBytes);

        if (!await LoadExactAsync(sizeof(uint)))
        {
            return Guid.Empty;
        }

        var publicKeyLength = _reader.ReadUInt32();
        if (publicKeyLength == 0 || publicKeyLength > MaxPublicKeyBytes)
        {
            throw new InvalidOperationException($"相手の公開鍵サイズが不正です ({publicKeyLength} バイト)。");
        }

        if (!await LoadExactAsync(publicKeyLength))
        {
            return Guid.Empty;
        }

        var remotePublicKey = new byte[(int)publicKeyLength];
        _reader.ReadBytes(remotePublicKey);

        var sharedKey = EcdhService.CreateSharedKey(localKey, remotePublicKey);
        try
        {
            _cryptoSession?.Dispose();
            _cryptoSession = new CryptoSession(sharedKey);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(sharedKey);
        }

        return remoteSessionId;
    }

    public async Task SendAsync(string message)
    {
        ThrowIfDisposed();
        var cryptoSession = _cryptoSession
            ?? throw new InvalidOperationException("暗号化ハンドシェイクが完了していません。");

        var encryptedData = cryptoSession.Encrypt(message);
        if (encryptedData.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"送信メッセージが大きすぎます ({encryptedData.Length} バイト)。");
        }

        await _writeLock.WaitAsync();
        try
        {
            _writer.WriteUInt32((uint)encryptedData.Length);
            _writer.WriteBytes(encryptedData);
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> ReceiveAsync()
    {
        ThrowIfDisposed();
        var cryptoSession = _cryptoSession
            ?? throw new InvalidOperationException("暗号化ハンドシェイクが完了していません。");

        if (!await LoadExactAsync(sizeof(uint)))
        {
            return null;
        }

        var length = _reader.ReadUInt32();
        if (length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"受信メッセージが大きすぎます ({length} バイト)。");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        if (!await LoadExactAsync(length))
        {
            return null;
        }

        var encryptedData = new byte[(int)length];
        _reader.ReadBytes(encryptedData);
        return cryptoSession.Decrypt(encryptedData);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _reader.Dispose();
        _writer.Dispose();
        _socket.Dispose();
        _cryptoSession?.Dispose();
        _writeLock.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SocketMessageChannel));
        }
    }

    private async Task<bool> LoadExactAsync(uint byteCount)
    {
        return await _reader.LoadAsync(byteCount) == byteCount;
    }
}
