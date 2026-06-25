using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace chat
{
    public sealed partial class MainWindow : Window
    {
        private readonly BleDiscoveryService _bleDiscovery = new();
        private readonly WifiDirectChatService _wifiDirectChat;
        private readonly ObservableCollection<PeerViewModel> _peers = new();
        private string _advertisingStatus = "Advertising: Stopped";
        private string _scanningStatus = "Scanning: Stopped";
        private string _wifiServiceStatus = "Wi-Fi Direct: Stopped";
        private string _wifiConnectionStatus = "未接続";

        public MainWindow()
        {
            InitializeComponent();

            _wifiDirectChat = new WifiDirectChatService(_bleDiscovery.LocalSession);
            PeerList.ItemsSource = _peers;
            LocalSessionText.Text = $"Local session: {_bleDiscovery.SessionId}";

            _bleDiscovery.PublisherStatusChanged += BleDiscovery_PublisherStatusChanged;
            _bleDiscovery.WatcherStatusChanged += BleDiscovery_WatcherStatusChanged;
            _bleDiscovery.ErrorOccurred += Service_ErrorOccurred;
            _bleDiscovery.PeerDiscovered += BleDiscovery_PeerDiscovered;

            _wifiDirectChat.StatusChanged += WifiDirectChat_StatusChanged;
            _wifiDirectChat.ErrorOccurred += Service_ErrorOccurred;
            _wifiDirectChat.PeerDiscovered += WifiDirectChat_PeerDiscovered;
            _wifiDirectChat.MessageReceived += WifiDirectChat_MessageReceived;
            _wifiDirectChat.ConnectionStateChanged += WifiDirectChat_ConnectionStateChanged;

            Closed += MainWindow_Closed;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageBox.Text;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            MessageBox.Text = "";

            if (!_wifiDirectChat.IsConnected)
            {
                ErrorText.Text = "未接続です。Wi-Fi Direct 接続後に送信してください。";
                return;
            }

            await _wifiDirectChat.SendMessageAsync(message);
            ChatList.Items.Add($"Me: {message}");
        }

        private void StartAdvertiseButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            _bleDiscovery.StartAdvertising();
            SyncButtons();
        }

        private void StopAdvertiseButton_Click(object sender, RoutedEventArgs e)
        {
            _bleDiscovery.StopAdvertising();
            SyncButtons();
        }

        private void StartScanButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            _bleDiscovery.StartScanning();
            SyncButtons();
        }

        private void StopScanButton_Click(object sender, RoutedEventArgs e)
        {
            _bleDiscovery.StopScanning();
            SyncButtons();
        }

        private async void StartWifiButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            await _wifiDirectChat.StartAsync();
            SyncButtons();
        }

        private void StopWifiButton_Click(object sender, RoutedEventArgs e)
        {
            _wifiDirectChat.Stop();
            SyncButtons();
        }

        private async void ConnectWifiButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = "";
            if (PeerList.SelectedItem is PeerViewModel { WifiDirectPeer: not null } peer)
            {
                await _wifiDirectChat.ConnectToPeerAsync(peer.WifiDirectPeer);
            }

            SyncButtons();
        }

        private void DisconnectWifiButton_Click(object sender, RoutedEventArgs e)
        {
            _wifiDirectChat.CloseConnection();
            SyncButtons();
        }

        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncButtons();
        }

        private void BleDiscovery_PublisherStatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _advertisingStatus = status;
                UpdateStatusText();
                SyncButtons();
            });
        }

        private void BleDiscovery_WatcherStatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _scanningStatus = status;
                UpdateStatusText();
                SyncButtons();
            });
        }

        private void BleDiscovery_PeerDiscovered(object? sender, BlePeer peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpsertBlePeer(peer);
                SyncButtons();
            });

            _ = _wifiDirectChat.AddBlePeerAsync(peer);
        }

        private void WifiDirectChat_StatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _wifiServiceStatus = status;
                UpdateStatusText();
                SyncButtons();
            });
        }

        private void WifiDirectChat_PeerDiscovered(object? sender, WifiDirectPeer peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpsertWifiPeer(peer);
                SyncButtons();
            });
        }

        private void WifiDirectChat_MessageReceived(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() => ChatList.Items.Add($"Peer: {message}"));
        }

        private void WifiDirectChat_ConnectionStateChanged(object? sender, bool isConnected)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _wifiConnectionStatus = isConnected ? "接続済み" : "未接続";
                UpdateStatusText();
                SyncButtons();
            });
        }

        private void Service_ErrorOccurred(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() => ErrorText.Text = message);
        }

        private void UpsertBlePeer(BlePeer peer)
        {
            var viewModel = FindPeer(peer.SessionId, peer.Nonce);
            if (viewModel is null)
            {
                _peers.Add(new PeerViewModel(peer));
                return;
            }

            viewModel.UpdateBle(peer);
        }

        private void UpsertWifiPeer(WifiDirectPeer peer)
        {
            var viewModel = FindPeer(peer.Session.SessionId, peer.Session.Nonce);
            if (viewModel is null)
            {
                _peers.Add(new PeerViewModel(peer));
                return;
            }

            viewModel.UpdateWifi(peer);
        }

        private PeerViewModel? FindPeer(Guid sessionId, uint nonce)
        {
            foreach (var peer in _peers)
            {
                if (peer.SessionId == sessionId && peer.Nonce == nonce)
                {
                    return peer;
                }
            }

            return null;
        }

        private void UpdateStatusText()
        {
            BleStatusText.Text = $"BLE: {_advertisingStatus} / {_scanningStatus}";
            WifiStatusText.Text = $"{_wifiServiceStatus} / {_wifiConnectionStatus}";
        }

        private void SyncButtons()
        {
            StartAdvertiseButton.IsEnabled = !_bleDiscovery.IsAdvertising;
            StopAdvertiseButton.IsEnabled = _bleDiscovery.IsAdvertising;
            StartScanButton.IsEnabled = !_bleDiscovery.IsScanning;
            StopScanButton.IsEnabled = _bleDiscovery.IsScanning;
            StartWifiButton.IsEnabled = !_wifiDirectChat.IsStarted;
            StopWifiButton.IsEnabled = _wifiDirectChat.IsStarted;
            ConnectWifiButton.IsEnabled = _wifiDirectChat.IsStarted &&
                !_wifiDirectChat.IsConnected &&
                PeerList.SelectedItem is PeerViewModel { WifiDirectPeer: not null };
            DisconnectWifiButton.IsEnabled = _wifiDirectChat.IsConnected;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _wifiDirectChat.Dispose();
            _bleDiscovery.Dispose();
        }

        private sealed class PeerViewModel
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
            }

            public void UpdateWifi(WifiDirectPeer peer)
            {
                WifiDirectPeer = peer;
            }
        }
    }
}
