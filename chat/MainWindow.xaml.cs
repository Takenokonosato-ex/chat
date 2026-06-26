using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace chat
{
    public sealed partial class MainWindow : Window
    {
        private const long MaxFileBytes = 10 * 1024 * 1024;

        private readonly BleDiscoveryService _bleDiscovery = new();
        private readonly WifiDirectChatService _wifiDirectChat;
        private readonly ObservableCollection<PeerViewModel> _peers = new();
        private string _advertisingStatus = "広告停止";
        private string _scanningStatus = "スキャン停止";
        private string _wifiServiceStatus = "Wi-Fi Direct: 停止";
        private string _wifiConnectionStatus = "未接続";

        public MainWindow()
        {
            InitializeComponent();

            _wifiDirectChat = new WifiDirectChatService(_bleDiscovery.LocalSession);
            PeerList.ItemsSource = _peers;
            LocalSessionText.Text = $"ローカルセッション: {_bleDiscovery.SessionId}";
            Root.RequestedTheme = ElementTheme.Light;

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
            UpdateStatusText();
            SyncButtons();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentMessageAsync();
        }

        private async void PickFileButton_Click(object sender, RoutedEventArgs e)
        {
            ClearError();

            if (!_wifiDirectChat.IsConnected)
            {
                ShowError("未接続です。Wi-Fi Directで相手に接続してからファイルを送信してください。");
                return;
            }

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > MaxFileBytes)
            {
                ShowError($"送信できるファイルは最大 {FormatBytes(MaxFileBytes)} です。選択したファイル: {FormatBytes((long)properties.Size)}");
                return;
            }

            var buffer = await FileIO.ReadBufferAsync(file);
            var content = buffer.ToArray();
            await _wifiDirectChat.SendFileAsync(file.Name, file.ContentType, content);
            AddChatLine("自分", $"ファイルを送信: {file.Name} ({FormatBytes(content.LongLength)})", DateTimeOffset.Now);
        }

        private async void MessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            await SendCurrentMessageAsync();
        }

        private async System.Threading.Tasks.Task SendCurrentMessageAsync()
        {
            var message = MessageBox.Text;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            MessageBox.Text = "";

            if (!_wifiDirectChat.IsConnected)
            {
                ShowError("未接続です。Wi-Fi Directで相手に接続してから送信してください。");
                return;
            }

            await _wifiDirectChat.SendMessageAsync(message);
            AddChatLine("自分", message, DateTimeOffset.Now);
        }

        private void StartAdvertiseButton_Click(object sender, RoutedEventArgs e)
        {
            ClearError();
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
            ClearError();
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
            ClearError();
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
            ClearError();
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

        private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            Root.RequestedTheme = ThemeToggle.IsOn ? ElementTheme.Dark : ElementTheme.Light;
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

        private void WifiDirectChat_MessageReceived(object? sender, ChatWireMessage message)
        {
            DispatcherQueue.TryEnqueue(async () => await HandleReceivedMessageAsync(message));
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
            DispatcherQueue.TryEnqueue(() => ShowError(message));
        }

        private async System.Threading.Tasks.Task HandleReceivedMessageAsync(ChatWireMessage message)
        {
            if (message.Kind == ChatWireMessage.FileKind)
            {
                await SaveReceivedFileAsync(message);
                return;
            }

            AddChatLine("相手", message.Text ?? "", message.SentAt);
        }

        private async System.Threading.Tasks.Task SaveReceivedFileAsync(ChatWireMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.FileName) || string.IsNullOrWhiteSpace(message.DataBase64))
            {
                AddChatLine("相手", "ファイルを受信しましたが、データが不完全でした。", message.SentAt);
                return;
            }

            try
            {
                var fileName = Path.GetFileName(message.FileName);
                var content = Convert.FromBase64String(message.DataBase64);
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("ReceivedFiles", CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteBytesAsync(file, content);

                AddChatLine("相手", $"ファイルを受信: {file.Name} ({FormatBytes(content.LongLength)})\n保存先: {file.Path}", message.SentAt);
            }
            catch (Exception ex)
            {
                ShowError($"受信ファイルの保存に失敗しました: {ex.Message}");
            }
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

        private void AddChatLine(string sender, string message, DateTimeOffset sentAt)
        {
            ChatList.Items.Add($"[{sentAt:HH:mm:ss}] {sender}: {message}");
        }

        private void ShowError(string message)
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }

        private void ClearError()
        {
            ErrorBar.IsOpen = false;
            ErrorBar.Message = "";
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
            PickFileButton.IsEnabled = _wifiDirectChat.IsConnected;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            var size = (double)bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.#} {units[unit]}";
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _wifiDirectChat.Dispose();
            _bleDiscovery.Dispose();
        }

        private sealed class PeerViewModel : INotifyPropertyChanged
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

            public string DisplayText
            {
                get
                {
                    var shortSession = SessionId.ToString("N")[..8];
                    var rssi = BlePeer is null ? "BLE: 待機中" : $"BLE RSSI: {BlePeer.RawSignalStrengthInDBm} dBm";
                    var wifi = WifiDirectPeer is null ? "Wi-Fi Direct: 探索中" : $"Wi-Fi Direct: {WifiDirectPeer.DeviceInformation.Name}";
                    return $"{shortSession}  |  {rssi}  |  {wifi}";
                }
            }

            public void UpdateBle(BlePeer peer)
            {
                BlePeer = peer;
                OnPropertyChanged(nameof(DisplayText));
            }

            public void UpdateWifi(WifiDirectPeer peer)
            {
                WifiDirectPeer = peer;
                OnPropertyChanged(nameof(DisplayText));
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
