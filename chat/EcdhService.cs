using System;
using System.Security.Cryptography;

namespace chat;

public static class EcdhService
{
    public static ECDiffieHellman Create()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    public static byte[] GetPublicKey(ECDiffieHellman ecdh)
    {
        ArgumentNullException.ThrowIfNull(ecdh);
        return ecdh.ExportSubjectPublicKeyInfo();
    }

    public static byte[] CreateSharedKey(ECDiffieHellman myKey, byte[] otherPublicKey)
    {
        ArgumentNullException.ThrowIfNull(myKey);

        if (otherPublicKey is null || otherPublicKey.Length == 0)
        {
            throw new ArgumentException("相手の公開鍵が不正です。", nameof(otherPublicKey));
        }

        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublicKey, out _);

        var sharedSecret = myKey.DeriveKeyMaterial(other.PublicKey);
        try
        {
            return SHA256.HashData(sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }
}
