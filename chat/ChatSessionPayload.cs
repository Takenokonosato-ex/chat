using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.WiFiDirect;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace chat;

public readonly record struct ChatSessionPayload(Guid SessionId, uint Nonce)
{
    public const byte Version = 1;
    public const ushort BleCompanyId = 0xFFFF;
    public const byte WifiDirectOuiType = 0x43;
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CH");
    public static readonly byte[] WifiDirectOui = { 0x00, 0x50, 0xF2 };

    public static ChatSessionPayload CreateLocal() => new(EncodeNameToGuid(Environment.MachineName), CreateNonce());

    public static Guid EncodeNameToGuid(string name)
    {
        var bytes = new byte[16];
        var nameBytes = Encoding.UTF8.GetBytes(name);

        int safeLength = 0;
        int i = 0;
        while (i < nameBytes.Length)
        {
            byte b = nameBytes[i];
            int charLen;
            if ((b & 0x80) == 0) charLen = 1;
            else if ((b & 0xE0) == 0xC0) charLen = 2;
            else if ((b & 0xF0) == 0xE0) charLen = 3;
            else if ((b & 0xF8) == 0xF0) charLen = 4;
            else charLen = 1;

            if (safeLength + charLen <= 16)
            {
                safeLength += charLen;
                i += charLen;
            }
            else
            {
                break;
            }
        }

        Array.Copy(nameBytes, 0, bytes, 0, safeLength);
        return new Guid(bytes);
    }

    public static string DecodeNameFromGuid(Guid guid)
    {
        var bytes = guid.ToByteArray();
        int len = Array.IndexOf<byte>(bytes, 0);
        if (len < 0) len = 16;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    public IBuffer ToBuffer()
    {
        using var writer = new DataWriter
        {
            ByteOrder = ByteOrder.LittleEndian
        };
        writer.WriteBytes(Magic);
        writer.WriteByte(Version);
        
        // 通信データを小さくするため、Nonceはushort (2バイト) に縮めます。
        writer.WriteUInt16((ushort)(Nonce & 0xFFFF));

        // 16バイトのGuid全体ではなくPC名を書き込みます。
        string pcName = DecodeNameFromGuid(SessionId);
        byte[] nameBytes = Encoding.UTF8.GetBytes(pcName);
        byte nameLen = (byte)Math.Min(nameBytes.Length, 15); // 最大15バイト
        writer.WriteByte(nameLen);
        writer.WriteBytes(nameBytes.Take(nameLen).ToArray());

        return writer.DetachBuffer();
    }



    public static bool TryParse(IBuffer payload, out ChatSessionPayload session)
    {
        session = default;

        // 最小長: Magic(2) + Version(1) + Nonce(2) + NameLen(1) = 6バイト
        if (payload.Length < 6)
        {
            return false;
        }

        var reader = DataReader.FromBuffer(payload);
        reader.ByteOrder = ByteOrder.LittleEndian;

        var magic = new byte[Magic.Length];
        reader.ReadBytes(magic);

        if (!magic.SequenceEqual(Magic))
        {
            return false;
        }

        var version = reader.ReadByte();
        if (version != Version)
        {
            return false;
        }

        ushort shortNonce = reader.ReadUInt16();
        byte nameLen = reader.ReadByte();

        if (payload.Length < 6 + nameLen)
        {
            return false;
        }

        var nameBytes = new byte[nameLen];
        reader.ReadBytes(nameBytes);
        string pcName = Encoding.UTF8.GetString(nameBytes);

        // PC名からGuidを復元します。
        Guid sessionId = EncodeNameToGuid(pcName);
        session = new ChatSessionPayload(sessionId, shortNonce);
        return true;
    }

    public static bool IsOurWifiDirectElement(WiFiDirectInformationElement element)
    {
        return element.OuiType == WifiDirectOuiType &&
            element.Oui.ToArray().SequenceEqual(WifiDirectOui);
    }

    private static uint CreateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(sizeof(uint));
        return BitConverter.ToUInt32(bytes);
    }
}
