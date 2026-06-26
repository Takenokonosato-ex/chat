using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace chat;

public sealed class SocketMessageChannel : IDisposable
{
    private readonly StreamSocket _socket;
    private readonly DataReader _reader;
    private readonly DataWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public SocketMessageChannel(StreamSocket socket)
    {
        _socket = socket;
        _reader = new DataReader(socket.InputStream)
        {
            ByteOrder = ByteOrder.LittleEndian,
            UnicodeEncoding = UnicodeEncoding.Utf8,
            InputStreamOptions = InputStreamOptions.Partial
        };
        _writer = new DataWriter(socket.OutputStream)
        {
            ByteOrder = ByteOrder.LittleEndian,
            UnicodeEncoding = UnicodeEncoding.Utf8
        };
    }

    public async Task<Guid> HandshakeAsync(Guid localSessionId)
    {
        // 自分のセッションIDを送る (16 bytes raw)
        await _writeLock.WaitAsync();
        try
        {
            _writer.WriteBytes(localSessionId.ToByteArray());
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        // 相手のセッションIDを受け取る (16 bytes raw)
        var loaded = await _reader.LoadAsync(16);
        if (loaded < 16)
        {
            return Guid.Empty;
        }

        var guidBytes = new byte[16];
        _reader.ReadBytes(guidBytes);
        return new Guid(guidBytes);
    }

    public async Task SendAsync(string message)
    {
        ThrowIfDisposed();

        await _writeLock.WaitAsync();
        try
        {
            _writer.WriteUInt32(_writer.MeasureString(message));
            _writer.WriteString(message);
            await _writer.StoreAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> ReceiveAsync()
    {
        ThrowIfDisposed();

        var loaded = await _reader.LoadAsync(sizeof(uint));
        if (loaded < sizeof(uint))
        {
            return null;
        }

        var length = _reader.ReadUInt32();
        if (length == 0)
        {
            return string.Empty;
        }

        loaded = await _reader.LoadAsync(length);
        if (loaded < length)
        {
            return null;
        }

        return _reader.ReadString(length);
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
}
