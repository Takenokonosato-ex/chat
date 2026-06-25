using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;

namespace chat
{
    public sealed partial class MainWindow : Window
    {
        private readonly BleDiscoveryService _bleDiscovery = new();
        private readonly ObservableCollection<BlePeer> _peers = new();
        private string _advertisingStatus = "Advertising: Stopped";
        private string _scanningStatus = "Scanning: Stopped";

        public MainWindow()
        {
            InitializeComponent();

            PeerList.ItemsSource = _peers;
            LocalSessionText.Text = $"Local session: {_bleDiscovery.SessionId}";

            _bleDiscovery.PublisherStatusChanged += BleDiscovery_PublisherStatusChanged;
            _bleDiscovery.WatcherStatusChanged += BleDiscovery_WatcherStatusChanged;
            _bleDiscovery.ErrorOccurred += BleDiscovery_ErrorOccurred;
            _bleDiscovery.PeerDiscovered += BleDiscovery_PeerDiscovered;
            Closed += MainWindow_Closed;
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageBox.Text))
            {
                return;
            }

            ChatList.Items.Add($"Me: {MessageBox.Text}");
            MessageBox.Text = "";
        }

        private void StartAdvertiseButton_Click(object sender, RoutedEventArgs e)
        {
            BleErrorText.Text = "";
            _bleDiscovery.StartAdvertising();
            SyncBleButtons();
        }

        private void StopAdvertiseButton_Click(object sender, RoutedEventArgs e)
        {
            _bleDiscovery.StopAdvertising();
            SyncBleButtons();
        }

        private void StartScanButton_Click(object sender, RoutedEventArgs e)
        {
            BleErrorText.Text = "";
            _bleDiscovery.StartScanning();
            SyncBleButtons();
        }

        private void StopScanButton_Click(object sender, RoutedEventArgs e)
        {
            _bleDiscovery.StopScanning();
            SyncBleButtons();
        }

        private void BleDiscovery_PublisherStatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _advertisingStatus = status;
                UpdateStatusText();
                SyncBleButtons();
            });
        }

        private void BleDiscovery_WatcherStatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _scanningStatus = status;
                UpdateStatusText();
                SyncBleButtons();
            });
        }

        private void BleDiscovery_ErrorOccurred(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() => BleErrorText.Text = message);
        }

        private void BleDiscovery_PeerDiscovered(object? sender, BlePeer peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                for (var i = 0; i < _peers.Count; i++)
                {
                    if (_peers[i].SessionId == peer.SessionId && _peers[i].Nonce == peer.Nonce)
                    {
                        _peers[i] = peer;
                        return;
                    }
                }

                _peers.Add(peer);
            });
        }

        private void UpdateStatusText()
        {
            BleStatusText.Text = $"{_advertisingStatus} / {_scanningStatus}";
        }

        private void SyncBleButtons()
        {
            StartAdvertiseButton.IsEnabled = !_bleDiscovery.IsAdvertising;
            StopAdvertiseButton.IsEnabled = _bleDiscovery.IsAdvertising;
            StartScanButton.IsEnabled = !_bleDiscovery.IsScanning;
            StopScanButton.IsEnabled = _bleDiscovery.IsScanning;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _bleDiscovery.Dispose();
        }
    }
}
