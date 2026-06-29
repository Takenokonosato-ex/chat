using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace chat
{
    public sealed class ChatMessageViewModel
    {
        public string Sender { get; set; } = "";
        public string Message { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public bool IsMe { get; set; }
        public string SenderInitial => string.IsNullOrEmpty(Sender) ? "?" : Sender[..1].ToUpperInvariant();
    }

    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? SentTemplate { get; set; }
        public DataTemplate? ReceivedTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            return item is ChatMessageViewModel { IsMe: true } ? SentTemplate! : ReceivedTemplate!;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return SelectTemplateCore(item);
        }
    }

    public sealed class PeerViewModel : INotifyPropertyChanged
    {
        public PeerViewModel(BlePeer peer)
        {
            SessionId = peer.SessionId;
            Nonce = peer.Nonce;
            BlePeer = peer;
        }

        public PeerViewModel(WifiDirectPeer peer)
        {
            SessionId = peer.Session.SessionId;
            Nonce = peer.Session.Nonce;
            WifiDirectPeer = peer;
        }

        public Guid SessionId { get; }
        public uint Nonce { get; }
        public BlePeer? BlePeer { get; private set; }
        public WifiDirectPeer? WifiDirectPeer { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ShortId => SessionId.ToString()[..8];
        public string Initial => SessionId.ToString()[..1].ToUpperInvariant();
        public string RssiText => BlePeer is null
            ? "BLE: 未発見"
            : $"RSSI: {BlePeer.RawSignalStrengthInDBm} dBm";
        public string WifiStatusText => WifiDirectPeer is null
            ? "Wi-Fi: 探索中"
            : WifiDirectPeer.DeviceInformation.Name;

        public string DisplayText
        {
            get
            {
                var rssi = BlePeer is null ? "BLE: 未発見" : $"BLE RSSI: {BlePeer.RawSignalStrengthInDBm} dBm";
                var wifi = WifiDirectPeer is null ? "Wi-Fi Direct: 探索中" : $"Wi-Fi Direct: {WifiDirectPeer.DeviceInformation.Name}";
                return $"{SessionId}  {rssi}  {wifi}";
            }
        }

        public void UpdateBle(BlePeer peer)
        {
            BlePeer = peer;
            OnPropertyChanged(nameof(BlePeer));
            OnPropertyChanged(nameof(RssiText));
            OnPropertyChanged(nameof(DisplayText));
        }

        public void UpdateWifi(WifiDirectPeer peer)
        {
            WifiDirectPeer = peer;
            OnPropertyChanged(nameof(WifiDirectPeer));
            OnPropertyChanged(nameof(WifiStatusText));
            OnPropertyChanged(nameof(DisplayText));
        }

        public void RefreshWifi()
        {
            OnPropertyChanged(nameof(WifiStatusText));
            OnPropertyChanged(nameof(DisplayText));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
