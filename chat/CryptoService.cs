using System;
using System.Security.Cryptography;
using System.Text;

namespace chat;

public static class CryptoService
{
    public const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    public static byte[] Encrypt(string plainText, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ValidateKey(key);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var cipherText = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plainBytes, cipherText, tag);

        var result = new byte[NonceSizeBytes + TagSizeBytes + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipherText, 0, result, NonceSizeBytes + TagSizeBytes, cipherText.Length);
        return result;
    }

    public static string Decrypt(byte[] encryptedData, byte[] key)
    {
        if (encryptedData is null || encryptedData.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new ArgumentException("暗号データが不正です。", nameof(encryptedData));
        }

        ValidateKey(key);

        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        var cipherText = new byte[encryptedData.Length - NonceSizeBytes - TagSizeBytes];

        Buffer.BlockCopy(encryptedData, 0, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(encryptedData, NonceSizeBytes, tag, 0, TagSizeBytes);
        Buffer.BlockCopy(encryptedData, NonceSizeBytes + TagSizeBytes, cipherText, 0, cipherText.Length);

        var plainBytes = new byte[cipherText.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, cipherText, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    public static string EncryptToBase64(string plainText, byte[] key)
    {
        return Convert.ToBase64String(Encrypt(plainText, key));
    }

    public static string DecryptFromBase64(string encryptedBase64, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
        {
            throw new ArgumentException("暗号化されたBase64文字列が空です。", nameof(encryptedBase64));
        }

        return Decrypt(Convert.FromBase64String(encryptedBase64), key);
    }

    private static void ValidateKey(byte[] key)
    {
        if (key is null || key.Length != KeySizeBytes)
        {
            throw new ArgumentException("AES-256では32バイトの鍵が必要です。", nameof(key));
        }
    }
}
