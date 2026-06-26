using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;

namespace chat
{
    public sealed partial class MainWindow : Window
    {
        private readonly BleDiscoveryService _bleDiscovery = new();
        private readonly WifiDirectChatService _wifiDirectChat;
        private readonly ObservableCollection<PeerViewModel> _peers = new();
        private readonly ObservableCollection<ChatMessageViewModel> _chatMessages = new();
        private string _advertisingStatus = "Stopped";
        private string _scanningStatus = "Stopped";
        private string _wifiServiceStatus = "Stopped";
        private string _wifiConnectionStatus = "未接続";
        private bool _isUpdatingUI;

        // Status indicator brushes
        private static readonly SolidColorBrush _successBrush = new(Color.FromArgb(255, 87, 242, 135));
        private static readonly SolidColorBrush _errorBrush = new(Color.FromArgb(255, 237, 66, 69));
        private static readonly SolidColorBrush _inactiveBrush = new(Color.FromArgb(255, 148, 155, 164));

        // Pulse animations for status dots
        private Storyboard? _blePulse;
        private Storyboard? _wifiPulse;
        private Storyboard? _connectedPulse;

        public MainWindow()
        {
            InitializeComponent();

            _wifiDirectChat = new WifiDirectChatService(_bleDiscovery.LocalSession);
            PeerList.ItemsSource = _peers;
            ChatList.ItemsSource = _chatMessages;
            LocalSessionText.Text = _bleDiscovery.SessionId.ToString();

            // Wire up BLE events
            _bleDiscovery.PublisherStatusChanged += BleDiscovery_PublisherStatusChanged;
            _bleDiscovery.WatcherStatusChanged += BleDiscovery_WatcherStatusChanged;
            _bleDiscovery.ErrorOccurred += Service_ErrorOccurred;
            _bleDiscovery.PeerDiscovered += BleDiscovery_PeerDiscovered;

            // Wire up Wi-Fi Direct events
            _wifiDirectChat.StatusChanged += WifiDirectChat_StatusChanged;
            _wifiDirectChat.ErrorOccurred += Service_ErrorOccurred;
            _wifiDirectChat.PeerDiscovered += WifiDirectChat_PeerDiscovered;
            _wifiDirectChat.MessageReceived += WifiDirectChat_MessageReceived;
            _wifiDirectChat.ConnectionStateChanged += WifiDirectChat_ConnectionStateChanged;

            Closed += MainWindow_Closed;

            InitializeAnimations();
        }

        // ─────────────────────────────────────────────
        // Animations
        // ─────────────────────────────────────────────

        private void InitializeAnimations()
        {
            _blePulse = CreatePulseAnimation(BleStatusDot, 800);
            _wifiPulse = CreatePulseAnimation(WifiStatusDot, 800);
            _connectedPulse = CreatePulseAnimation(ConnectionStatusDot, 1200);
        }

        private static Storyboard CreatePulseAnimation(DependencyObject target, int durationMs)
        {
            var animation = new DoubleAnimation
            {
                From = 1.0,
                To = 0.3,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            return storyboard;
        }

        // ─────────────────────────────────────────────
        // Send Message
        // ─────────────────────────────────────────────

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private void MessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                _ = SendMessageAsync();
                e.Handled = true;
            }
        }

        private async Task SendMessageAsync()
        {
            var message = MessageBox.Text;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            MessageBox.Text = "";

            if (!_wifiDirectChat.IsConnected)
            {
                ShowError("未接続です。Wi-Fi Direct 接続後に送信してください。");
                return;
            }

            await _wifiDirectChat.SendMessageAsync(message);
            _chatMessages.Add(new ChatMessageViewModel
            {
                Sender = "Me",
                Message = message,
                Timestamp = DateTime.Now.ToString("HH:mm"),
                IsMe = true
            });
            ScrollChatToBottom();
        }

        // ─────────────────────────────────────────────
        // Toggle / Button Handlers
        // ─────────────────────────────────────────────

        private void AdvertiseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            ClearError();
            if (AdvertiseToggle.IsOn)
                _bleDiscovery.StartAdvertising();
            else
                _bleDiscovery.StopAdvertising();
            SyncUI();
        }

        private void ScanToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            ClearError();
            if (ScanToggle.IsOn)
                _bleDiscovery.StartScanning();
            else
                _bleDiscovery.StopScanning();
            SyncUI();
        }

        private async void WifiServiceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            ClearError();
            if (WifiServiceToggle.IsOn)
                await _wifiDirectChat.StartAsync();
            else
                _wifiDirectChat.Stop();
            SyncUI();
        }

        private async void ConnectWifiButton_Click(object sender, RoutedEventArgs e)
        {
            ClearError();
            if (PeerList.SelectedItem is PeerViewModel { WifiDirectPeer: not null } peer)
            {
                await _wifiDirectChat.ConnectToPeerAsync(peer.WifiDirectPeer);
            }

            SyncUI();
        }

        private void DisconnectWifiButton_Click(object sender, RoutedEventArgs e)
        {
            _wifiDirectChat.CloseConnection();
            SyncUI();
        }

        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SyncUI();
        }

        private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            var isDark = ThemeToggle.IsOn;
            ThemeHelper.SetTheme(RootGrid, isDark ? ElementTheme.Dark : ElementTheme.Light);
            ThemeIcon.Glyph = isDark ? "\uE708" : "\uE706";
            ThemeLabel.Text = isDark ? "ダークモード" : "ライトモード";
        }

        // ─────────────────────────────────────────────
        // Service Event Handlers
        // ─────────────────────────────────────────────

        private void BleDiscovery_PublisherStatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _advertisingStatus = status;
                UpdateStatusDisplay();
                SyncUI();
            });
        }

        private void BleDiscovery_WatcherStatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _scanningStatus = status;
                UpdateStatusDisplay();
                SyncUI();
            });
        }

        private void BleDiscovery_PeerDiscovered(object? sender, BlePeer peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpsertBlePeer(peer);
                SyncUI();
                PeerCountText.Text = $"{_peers.Count} 台発見";
            });

            _ = _wifiDirectChat.AddBlePeerAsync(peer);
        }

        private void WifiDirectChat_StatusChanged(object? sender, string status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _wifiServiceStatus = status;
                UpdateStatusDisplay();
                SyncUI();
            });
        }

        private void WifiDirectChat_PeerDiscovered(object? sender, WifiDirectPeer peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpsertWifiPeer(peer);
                SyncUI();
                PeerCountText.Text = $"{_peers.Count} 台発見";
            });
        }

        private void WifiDirectChat_MessageReceived(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _chatMessages.Add(new ChatMessageViewModel
                {
                    Sender = "Peer",
                    Message = message,
                    Timestamp = DateTime.Now.ToString("HH:mm"),
                    IsMe = false
                });
                ScrollChatToBottom();
            });
        }

        private void WifiDirectChat_ConnectionStateChanged(object? sender, bool isConnected)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _wifiConnectionStatus = isConnected ? "接続済み" : "未接続";
                UpdateStatusDisplay();
                SyncUI();
            });
        }

        private void Service_ErrorOccurred(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(() => ShowError(message));
        }

        // ─────────────────────────────────────────────
        // Peer Management
        // ─────────────────────────────────────────────

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

        // ─────────────────────────────────────────────
        // UI Update Helpers
        // ─────────────────────────────────────────────

        private void UpdateStatusDisplay()
        {
            // — BLE status indicator —
            var bleActive = _bleDiscovery.IsAdvertising || _bleDiscovery.IsScanning;
            BleStatusLabel.Text = bleActive
                ? $"{_advertisingStatus} / {_scanningStatus}"
                : "停止中";
            BleStatusDot.Fill = bleActive ? _successBrush : _inactiveBrush;

            if (bleActive)
            {
                _blePulse?.Begin();
            }
            else
            {
                _blePulse?.Stop();
                BleStatusDot.Opacity = 1.0;
            }

            // — Wi-Fi Direct status indicator —
            var wifiActive = _wifiDirectChat.IsStarted;
            WifiStatusLabel.Text = wifiActive
                ? $"{_wifiServiceStatus} / {_wifiConnectionStatus}"
                : "停止中";
            WifiStatusDot.Fill = wifiActive ? _successBrush : _inactiveBrush;

            if (wifiActive)
            {
                _wifiPulse?.Begin();
            }
            else
            {
                _wifiPulse?.Stop();
                WifiStatusDot.Opacity = 1.0;
            }

            // — Connection status (chat header) —
            var connected = _wifiDirectChat.IsConnected;
            ConnectionStatusLabel.Text = connected ? "接続済み" : "未接続";
            ConnectionStatusDot.Fill = connected ? _successBrush : _errorBrush;

            if (connected)
            {
                _connectedPulse?.Begin();
            }
            else
            {
                _connectedPulse?.Stop();
                ConnectionStatusDot.Opacity = 1.0;
            }
        }

        private void SyncUI()
        {
            _isUpdatingUI = true;
            AdvertiseToggle.IsOn = _bleDiscovery.IsAdvertising;
            ScanToggle.IsOn = _bleDiscovery.IsScanning;
            WifiServiceToggle.IsOn = _wifiDirectChat.IsStarted;
            _isUpdatingUI = false;

            ConnectWifiButton.IsEnabled = _wifiDirectChat.IsStarted &&
                !_wifiDirectChat.IsConnected &&
                PeerList.SelectedItem is PeerViewModel { WifiDirectPeer: not null };
            DisconnectWifiButton.IsEnabled = _wifiDirectChat.IsConnected;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ClearError()
        {
            ErrorText.Text = "";
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void ScrollChatToBottom()
        {
            if (_chatMessages.Count > 0)
            {
                ChatList.ScrollIntoView(_chatMessages[^1]);
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _blePulse?.Stop();
            _wifiPulse?.Stop();
            _connectedPulse?.Stop();
            _wifiDirectChat.Dispose();
            _bleDiscovery.Dispose();
        }
    }
}
