using System;
using System.Security.Cryptography;

namespace CryptoTest;

public static class EcdhService
{
    /// <summary>
    /// ECDH鍵ペアを生成します。
    /// </summary>
    public static ECDiffieHellman Create()
    {
        return ECDiffieHellman.Create();
    }

    /// <summary>
    /// 公開鍵を取得します。
    /// </summary>
    public static byte[] GetPublicKey(ECDiffieHellman ecdh)
    {
        if (ecdh == null)
        {
            throw new ArgumentNullException(nameof(ecdh));
        }

        return ecdh.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// 公開鍵をBase64文字列で取得します。
    /// </summary>
    public static string GetPublicKeyBase64(ECDiffieHellman ecdh)
    {
        return Convert.ToBase64String(GetPublicKey(ecdh));
    }

    /// <summary>
    /// 相手の公開鍵から共通鍵を生成します。
    /// </summary>
    public static byte[] CreateSharedKey(
        ECDiffieHellman myKey,
        byte[] otherPublicKey)
    {
        if (myKey == null)
        {
            throw new ArgumentNullException(nameof(myKey));
        }

        if (otherPublicKey == null || otherPublicKey.Length == 0)
        {
            throw new ArgumentException(
                "相手の公開鍵が不正です。",
                nameof(otherPublicKey));
        }

        using ECDiffieHellman other =
            ECDiffieHellman.Create();

        other.ImportSubjectPublicKeyInfo(
            otherPublicKey,
            out _);

        return myKey.DeriveKeyMaterial(other.PublicKey);
    }

    /// <summary>
    /// Base64形式の公開鍵から共通鍵を生成します。
    /// </summary>
    public static byte[] CreateSharedKeyFromBase64(
        ECDiffieHellman myKey,
        string otherPublicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(otherPublicKeyBase64))
        {
            throw new ArgumentException(
                "Base64形式の公開鍵が空です。",
                nameof(otherPublicKeyBase64));
        }

        byte[] otherPublicKey =
            Convert.FromBase64String(otherPublicKeyBase64);

        return CreateSharedKey(myKey, otherPublicKey);
    }
}