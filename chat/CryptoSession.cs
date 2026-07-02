using System;
using System.Security.Cryptography;

namespace chat;

public sealed class CryptoSession : IDisposable
{
    private readonly byte[] _sharedKey;
    private bool _disposed;

    public CryptoSession(byte[] sharedKey)
    {
        if (sharedKey is null || sharedKey.Length != CryptoService.KeySizeBytes)
        {
            throw new ArgumentException("AES-256では32バイトの共通鍵が必要です。", nameof(sharedKey));
        }

        _sharedKey = (byte[])sharedKey.Clone();
    }

    public byte[] Encrypt(string message)
    {
        ThrowIfDisposed();
        return CryptoService.Encrypt(message, _sharedKey);
    }

    public string Decrypt(byte[] encryptedData)
    {
        ThrowIfDisposed();
        return CryptoService.Decrypt(encryptedData, _sharedKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_sharedKey);
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CryptoSession));
        }
    }
}
