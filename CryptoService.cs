using System;
using System.Security.Cryptography;
using System.Text;

namespace CryptoTest;

public static class CryptoService
{
    /// <summary>
    /// AES-GCMを使用して文字列を暗号化します。
    /// </summary>
    /// <param name="plainText">暗号化する文字列</param>
    /// <param name="key">32バイトの共通鍵</param>
    /// <returns>Nonce + Tag + CipherText を連結したバイト列</returns>
    public static byte[] Encrypt(string plainText, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            throw new ArgumentException(
                "メッセージが空です。",
                nameof(plainText));
        }

        if (key == null || key.Length != 32)
        {
            throw new ArgumentException(
                "AES-256では32バイトの鍵が必要です。",
                nameof(key));
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipherText = new byte[plainBytes.Length];
        byte[] tag = new byte[16];

        using var aes = new AesGcm(key);

        aes.Encrypt(
            nonce,
            plainBytes,
            cipherText,
            tag);

        byte[] result =
            new byte[nonce.Length + tag.Length + cipherText.Length];

        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);

        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);

        Buffer.BlockCopy(
            cipherText,
            0,
            result,
            nonce.Length + tag.Length,
            cipherText.Length);

        return result;
    }

    /// <summary>
    /// AES-GCMで暗号化されたデータを復号します。
    /// </summary>
    /// <param name="encryptedData">Nonce + Tag + CipherText を連結したバイト列</param>
    /// <param name="key">32バイトの共通鍵</param>
    /// <returns>復号した文字列</returns>
    public static string Decrypt(byte[] encryptedData, byte[] key)
    {
        if (encryptedData == null || encryptedData.Length < 28)
        {
            throw new ArgumentException(
                "暗号データが不正です。",
                nameof(encryptedData));
        }

        if (key == null || key.Length != 32)
        {
            throw new ArgumentException(
                "AES-256では32バイトの鍵が必要です。",
                nameof(key));
        }

        byte[] nonce = new byte[12];

        Buffer.BlockCopy(
            encryptedData,
            0,
            nonce,
            0,
            nonce.Length);

        byte[] tag = new byte[16];

        Buffer.BlockCopy(
            encryptedData,
            nonce.Length,
            tag,
            0,
            tag.Length);

        byte[] cipherText =
            new byte[encryptedData.Length - nonce.Length - tag.Length];

        Buffer.BlockCopy(
            encryptedData,
            nonce.Length + tag.Length,
            cipherText,
            0,
            cipherText.Length);

        byte[] plainBytes = new byte[cipherText.Length];

        using var aes = new AesGcm(key);

        aes.Decrypt(
            nonce,
            cipherText,
            tag,
            plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// AES-GCMで暗号化し、Base64文字列として返します。
    /// </summary>
    /// <param name="plainText">暗号化する文字列</param>
    /// <param name="key">32バイトの共通鍵</param>
    /// <returns>Base64形式の暗号化データ</returns>
    public static string EncryptToBase64(string plainText, byte[] key)
    {
        byte[] encryptedData = Encrypt(plainText, key);
        return Convert.ToBase64String(encryptedData);
    }

    /// <summary>
    /// Base64形式の暗号化データをAES-GCMで復号します。
    /// </summary>
    /// <param name="encryptedBase64">Base64形式の暗号化データ</param>
    /// <param name="key">32バイトの共通鍵</param>
    /// <returns>復号した文字列</returns>
    public static string DecryptFromBase64(string encryptedBase64, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
        {
            throw new ArgumentException(
                "暗号化されたBase64文字列が空です。",
                nameof(encryptedBase64));
        }

        byte[] encryptedData =
            Convert.FromBase64String(encryptedBase64);

        return Decrypt(encryptedData, key);
    }
}