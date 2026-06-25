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
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CHAT");
    public static readonly byte[] WifiDirectOui = { 0x00, 0x50, 0xF2 };

    public static ChatSessionPayload CreateLocal() => new(Guid.NewGuid(), CreateNonce());

    public IBuffer ToBuffer()
    {
        using var writer = new DataWriter
        {
            ByteOrder = ByteOrder.LittleEndian
        };
        writer.WriteBytes(Magic);
        writer.WriteByte(Version);
        writer.WriteBytes(SessionId.ToByteArray());
        writer.WriteUInt32(Nonce);
        return writer.DetachBuffer();
    }

    public WiFiDirectInformationElement ToWifiDirectInformationElement()
    {
        return new WiFiDirectInformationElement
        {
            Oui = CryptographicBuffer.CreateFromByteArray(WifiDirectOui),
            OuiType = WifiDirectOuiType,
            Value = ToBuffer()
        };
    }

    public static bool TryParse(IBuffer payload, out ChatSessionPayload session)
    {
        session = default;

        if (payload.Length < Magic.Length + 1 + 16 + sizeof(uint))
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

        var guidBytes = new byte[16];
        reader.ReadBytes(guidBytes);
        session = new ChatSessionPayload(new Guid(guidBytes), reader.ReadUInt32());
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
