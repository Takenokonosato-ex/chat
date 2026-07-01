namespace CryptoTest;

/// <summary>
/// 1つの通信相手との暗号化セッションを表します。
/// </summary>
public sealed class CryptoSession
{
    private readonly byte[] _sharedKey;

    /// <summary>
    /// 共通鍵を使用して暗号化セッションを作成します。
    /// </summary>
    /// <param name="sharedKey">32バイトの共通鍵</param>
    public CryptoSession(byte[] sharedKey)
    {
        if (sharedKey == null || sharedKey.Length != 32)
        {
            throw new ArgumentException(
                "AES-256では32バイトの共通鍵が必要です。",
                nameof(sharedKey));
        }

        _sharedKey = sharedKey;
    }

    /// <summary>
    /// メッセージを暗号化してBase64文字列で返します。
    /// </summary>
    public string EncryptToBase64(string message)
    {
        return CryptoService.EncryptToBase64(message, _sharedKey);
    }

    /// <summary>
    /// Base64形式の暗号データを復号します。
    /// </summary>
    public string DecryptFromBase64(string encryptedBase64)
    {
        return CryptoService.DecryptFromBase64(encryptedBase64, _sharedKey);
    }

    /// <summary>
    /// 現在使用している共通鍵を取得します。
    /// </summary>
    public byte[] SharedKey
    {
        get
        {
            return (byte[])_sharedKey.Clone();
        }
    }
    /// <summary>
    /// メッセージを暗号化します。
    /// </summary>
    public byte[] Encrypt(string message)
    {
        return CryptoService.Encrypt(
            message,
            _sharedKey);
    }
    /// <summary>
    /// 暗号データを復号します。
    /// </summary>
    public string Decrypt(byte[] encryptedData)
    {
        return CryptoService.Decrypt(
            encryptedData,
            _sharedKey);
    }
}