using System;
using Windows.Devices.Enumeration;

namespace chat;

public sealed class WifiDirectPeer
{
    public WifiDirectPeer(DeviceInformation deviceInformation, ChatSessionPayload session)
    {
        DeviceInformation = deviceInformation;
        Session = session;
    }

    public DeviceInformation DeviceInformation { get; private set; }

    public ChatSessionPayload Session { get; }

    public string DisplayName =>
        $"{Session.SessionId}  Wi-Fi Direct: {DeviceInformation.Name}  {(DeviceInformation.Pairing.IsPaired ? "Paired" : "Unpaired")}";

    public void Update(DeviceInformationUpdate update)
    {
        DeviceInformation.Update(update);
    }
}
