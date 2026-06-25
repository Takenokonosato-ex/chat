using System;

namespace chat;

public sealed class BlePeer
{
    public BlePeer(ulong bluetoothAddress, Guid sessionId, uint nonce, short rawSignalStrengthInDBm, DateTimeOffset lastSeen)
    {
        BluetoothAddress = bluetoothAddress;
        SessionId = sessionId;
        Nonce = nonce;
        RawSignalStrengthInDBm = rawSignalStrengthInDBm;
        LastSeen = lastSeen;
    }

    public ulong BluetoothAddress { get; }

    public Guid SessionId { get; }

    public uint Nonce { get; }

    public short RawSignalStrengthInDBm { get; }

    public DateTimeOffset LastSeen { get; }

    public string DisplayText =>
        $"{SessionId}  RSSI: {RawSignalStrengthInDBm} dBm  Last seen: {LastSeen:HH:mm:ss}";
}
