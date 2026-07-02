using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Networking;
using Windows.Networking.Sockets;
using Microsoft.UI.Dispatching;

namespace chat;

public sealed class WifiDirectChatService : IDisposable
{
    private const string ChatPort = "50001";
    private const int SocketConnectAttempts = 10;
    private const int SocketConnectTimeoutMs = 10000;
    private const int SocketConnectRetryDelayMs = 1500;
    private const int PublisherStartTimeoutMs = 3000;
    private const int ConnectionRetryCooldownSeconds = 10;
    private const int DhcpAddressAssignmentDelayMs = 1500;
    private const int DhcpAddressRetryDelayMs = 1000;
    private static readonly string[] DeviceProperties =
    {
        "System.Devices.WiFiDirect.InformationElements"
    };
    private static readonly HostName[] FallbackHosts =
    {
        new("192.168.49.1"),
        new("192.168.137.1")
    };

    private readonly ChatSessionPayload _localSession;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _connectionGate = new();
    private readonly Dictionary<string, WifiDirectPeer> _wifiPeersByDeviceId = new();
    private readonly Dictionary<ChatSessionPayload, WifiDirectPeer> _wifiPeersBySession = new();
    private readonly Dictionary<ChatSessionPayload, BlePeer> _blePeers = new();
    private readonly Dictionary<string, DeviceInformation> _pendingDevicesById = new();
    private readonly Dictionary<ChatSessionPayload, DateTimeOffset> _lastConnectionAttempts = new();
    private readonly HashSet<ChatSessionPayload> _connectingSessions = new();
    private WiFiDirectAdvertisementPublisher? _publisher;
    private WiFiDirectConnectionListener? _connectionListener;
    private DeviceWatcher? _watcher;
    private WiFiDirectDevice? _wifiDirectDevice;
    private StreamSocketListener? _socketListener;
    private SocketMessageChannel? _channel;
    private bool _isStarted;
    private bool _isConnecting;
    private bool _disposed;

    public WifiDirectChatService(ChatSessionPayload localSession, DispatcherQueue dispatcherQueue)
    {
        _localSession = localSession;
        _dispatcherQueue = dispatcherQueue;
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<WifiDirectPeer>? PeerDiscovered;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsStarted => _isStarted;

    public bool IsConnected => _channel is not null;

    private void LogDebug(string msg)
    {
        Debug.WriteLine(msg);
        MessageReceived?.Invoke(this, $"[システム] {msg}");
    }

    public async Task StartAsync()
    {
        ThrowIfDisposed();

        if (_isStarted)
        {
            return;
        }

        try
        {
            LogDebug("開始処理: Wi-Fi Direct接続リスナーを作成しています...");
            _connectionListener = new WiFiDirectConnectionListener();
            _connectionListener.ConnectionRequested += ConnectionListener_ConnectionRequested;

            LogDebug("開始処理: デバイス監視を作成しています...");
            var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
            LogDebug($"開始処理: デバイスセレクターは '{selector}' です");
            _watcher = DeviceInformation.CreateWatcher(selector, DeviceProperties);
            _watcher.Added += Watcher_Added;
            _watcher.Updated += Watcher_Updated;
            _watcher.Removed += Watcher_Removed;
            _watcher.EnumerationCompleted += Watcher_EnumerationCompleted;
            _watcher.Stopped += Watcher_Stopped;

            LogDebug("開始処理: デバイス監視を開始しています...");
            _watcher.Start();

            _isStarted = true;
            LogDebug("開始処理: 正常に開始しました。");
            StatusChanged?.Invoke(this, "Wi-Fi Direct: 開始");

            _ = StartPublisherBestEffortAsync();
        }
        catch (Exception ex)
        {
            LogDebug($"開始処理: 開始に失敗しました: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Directの開始に失敗しました: {ex.Message}");
            Stop();
        }

        await Task.CompletedTask;
    }

    private async Task StartPublisherBestEffortAsync()
    {
        if (!_isStarted || _publisher is not null)
        {
            return;
        }

        StatusChanged?.Invoke(this, "Wi-Fi Direct: 広告を開始中");

        var startTask = Task.Run(() =>
        {
            LogDebug("広告: Wi-Fi Direct広告を初期化しています...");
            var publisher = new WiFiDirectAdvertisementPublisher();
            publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            publisher.Advertisement.InformationElements.Add(CreateSessionInformationElement());

            LogDebug("広告: 開始しています...");
            publisher.Start();
            return publisher;
        });

        var completedTask = await Task.WhenAny(startTask, Task.Delay(PublisherStartTimeoutMs));
        if (completedTask != startTask)
        {
            LogDebug("広告: 開始がタイムアウトしました。アプリを止めずに続行します。");
            StatusChanged?.Invoke(this, "Wi-Fi Direct: 広告開始がタイムアウト");
            _ = CompletePublisherStartAsync(startTask);
            return;
        }

        try
        {
            var publisher = await startTask;
            AcceptStartedPublisher(publisher);
        }
        catch (Exception ex)
        {
            LogDebug($"広告: 開始に失敗しました: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct広告の開始に失敗しました: {ex.Message}");
        }
    }

    private async Task CompletePublisherStartAsync(Task<WiFiDirectAdvertisementPublisher> startTask)
    {
        try
        {
            var publisher = await startTask;
            AcceptStartedPublisher(publisher);
        }
        catch (Exception ex)
        {
            LogDebug($"広告: 遅延開始に失敗しました: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct広告の開始に失敗しました: {ex.Message}");
        }
    }

    private void AcceptStartedPublisher(WiFiDirectAdvertisementPublisher publisher)
    {
        if (!_isStarted || _publisher is not null)
        {
            TryStopPublisher(publisher);
            return;
        }

        _publisher = publisher;
        _publisher.StatusChanged += Publisher_StatusChanged;
        LogDebug($"広告: 開始しました。状態={ToJapaneseAdvertisementStatus(_publisher.Status)}");
        StatusChanged?.Invoke(this, $"Wi-Fi Direct広告: {ToJapaneseAdvertisementStatus(_publisher.Status)}");
    }

    public void Stop()
    {
        CloseConnection();

        if (_watcher is not null)
        {
            _watcher.Added -= Watcher_Added;
            _watcher.Updated -= Watcher_Updated;
            _watcher.Removed -= Watcher_Removed;
            _watcher.EnumerationCompleted -= Watcher_EnumerationCompleted;
            _watcher.Stopped -= Watcher_Stopped;

            try
            {
                _watcher.Stop();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Wi-Fi Direct監視の停止に失敗しました: {ex.Message}");
            }

            _watcher = null;
        }

        if (_connectionListener is not null)
        {
            _connectionListener.ConnectionRequested -= ConnectionListener_ConnectionRequested;
            _connectionListener = null;
        }

        if (_publisher is not null)
        {
            _publisher.StatusChanged -= Publisher_StatusChanged;
            TryStopPublisher(_publisher);
            _publisher = null;
        }

        _wifiPeersByDeviceId.Clear();
        _wifiPeersBySession.Clear();
        _blePeers.Clear();
        _pendingDevicesById.Clear();
        _isStarted = false;
        _isConnecting = false;
        StatusChanged?.Invoke(this, "Wi-Fi Direct: 停止");
    }

    private bool TryBeginConnection(ChatSessionPayload session, bool force)
    {
        lock (_connectionGate)
        {
            if (IsConnected || _connectingSessions.Contains(session))
            {
                return false;
            }

            if (!force &&
                _lastConnectionAttempts.TryGetValue(session, out var lastAttempt) &&
                DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromSeconds(ConnectionRetryCooldownSeconds))
            {
                return false;
            }

            _connectingSessions.Add(session);
            _lastConnectionAttempts[session] = DateTimeOffset.UtcNow;
            _isConnecting = true;
            return true;
        }
    }

    private void EndConnection(ChatSessionPayload session)
    {
        lock (_connectionGate)
        {
            _connectingSessions.Remove(session);
            _isConnecting = _connectingSessions.Count > 0;
        }
    }

    private void TryStopPublisher(WiFiDirectAdvertisementPublisher publisher)
    {
        try
        {
            if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started ||
                publisher.Status == WiFiDirectAdvertisementPublisherStatus.Created)
            {
                publisher.Stop();
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct広告の停止に失敗しました: {ex.Message}");
        }
    }

    public async Task AddBlePeerAsync(BlePeer peer)
    {
        var session = new ChatSessionPayload(peer.SessionId, peer.Nonce);
        _blePeers[session] = peer;

        // BLEとWi-Fi Directで同じセッションを見つけている場合は自動接続します。
        if (_wifiPeersBySession.TryGetValue(session, out var wifiPeer))
        {
            await ConnectToPeerInternalAsync(wifiPeer, force: false);
        }
        else
        {
            // BLEより先に見つけたWi-Fi Directデバイスとの照合を試します。
            var newPeer = TryCreatePeerFromPendingDevices(session);
            if (newPeer != null)
            {
                PeerDiscovered?.Invoke(this, newPeer);
                StatusChanged?.Invoke(this, $"Wi-Fi Direct相手: {session.SessionId.ToString()[..8]}");
                await ConnectToPeerInternalAsync(newPeer, force: false);
            }
        }
    }

    public async Task ConnectToPeerAsync(WifiDirectPeer peer)
    {
        await ConnectToPeerInternalAsync(peer, force: true);
    }

    public async Task<bool> SendMessageAsync(string message)
    {
        if (_channel is null)
        {
            ErrorOccurred?.Invoke(this, "未接続です。送信前にWi-Fi Directで接続してください。");
            return false;
        }

        try
        {
            await _channel.SendAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"メッセージ送信に失敗しました: {ex.Message}");
            CloseConnection();
            return false;
        }
    }

    public void CloseConnection()
    {
        _channel?.Dispose();
        _channel = null;

        _socketListener?.Dispose();
        _socketListener = null;

        if (_wifiDirectDevice is not null)
        {
            _wifiDirectDevice.ConnectionStatusChanged -= WifiDirectDevice_ConnectionStatusChanged;
            _wifiDirectDevice.Dispose();
            _wifiDirectDevice = null;
        }

        ConnectionStateChanged?.Invoke(this, false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }



    private async Task ConnectToPeerInternalAsync(WifiDirectPeer peer, bool force)
    {
        if (IsConnected)
        {
            return;
        }

        if (!TryBeginConnection(peer.Session, force))
        {
            LogDebug($"[Wi-Fi Direct] {peer.DeviceInformation.Name} への重複接続をスキップしました");
            return;
        }

        StatusChanged?.Invoke(this, $"Wi-Fi Direct: {peer.DeviceInformation.Name} に接続中");

        try
        {
            var tcs = new TaskCompletionSource<WiFiDirectDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var connectionParams = new WiFiDirectConnectionParameters();
                    connectionParams.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);
                    connectionParams.PreferredPairingProcedure = WiFiDirectPairingProcedure.GroupOwnerNegotiation;
                    connectionParams.GroupOwnerIntent = (short)(ShouldInitiateConnection(peer.Session) ? 0 : 14);

                    LogDebug($"[Wi-Fi Direct] {peer.DeviceInformation.Name} とペアリング確認中");
                    bool paired = await EnsurePairedAsync(peer.DeviceInformation, connectionParams);
                    if (!paired)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }
                    LogDebug("[Wi-Fi Direct] ペアリング済みです。IDからWiFiDirectDeviceを作成します。");
                    WiFiDirectDevice? d = null;
                    try
                    {
                        d = await WiFiDirectDevice.FromIdAsync(
                            peer.DeviceInformation.Id,
                            connectionParams);
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[Wi-Fi Direct] FromIdAsyncに失敗しました: {ex.Message} (HResult={ex.HResult:X8})。ペアリング解除後に再試行します...");
                        try
                        {
                            var unpairResult = await peer.DeviceInformation.Pairing.UnpairAsync();
                            LogDebug($"[Wi-Fi Direct] ペアリング解除完了: 状態={unpairResult.Status}。クリーンアップのため1500ms待機します...");
                            await Task.Delay(1500);
                        }
                        catch (Exception unpairEx)
                        {
                            LogDebug($"[Wi-Fi Direct] ペアリング解除に失敗しました: {unpairEx.Message}");
                        }

                        paired = await EnsurePairedAsync(peer.DeviceInformation, connectionParams);
                        if (paired)
                        {
                            d = await WiFiDirectDevice.FromIdAsync(
                                peer.DeviceInformation.Id,
                                connectionParams);
                        }
                    }
                    tcs.TrySetResult(d);
                }
                catch (Exception ex)
                {
                    LogDebug($"[Wi-Fi Direct] 接続準備に失敗しました: {ex}");
                    ErrorOccurred?.Invoke(this, $"ペアリング確認エラー: {ex.Message}");
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Wi-Fi Direct接続準備をUIスレッドに登録できませんでした。"));
            }

            var device = await tcs.Task;
            if (device is null)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct接続に失敗しました: デバイスがnullです。");
                return;
            }

            SetWifiDirectDevice(device);
            var endpointPairs = await WaitForEndpointPairsAsync(device, "connection");
            if (endpointPairs.Count == 0)
            {
                return;
            }

            LogDebug($"[Wi-Fi Direct] エンドポイントを検出しました。相手={endpointPairs[0].RemoteHostName}:{ChatPort}");
            await Task.Delay(1000);

            await EnsureSocketListenerAsync(endpointPairs);

            if (ShouldInitiateConnection(peer.Session))
            {
                LogDebug("[Wi-Fi Direct] クライアントとして動作します。");
                var socketConnected = await TryConnectSocketsAsync(endpointPairs);
                if (!socketConnected)
                {
                    StatusChanged?.Invoke(this, "Wi-Fi Direct: 相手のソケット待ち");
                }
            }
            else
            {
                LogDebug("[Wi-Fi Direct] サーバーとして動作します。着信ソケットを待っています...");
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct接続に失敗しました: {ex.Message}。モバイルホットスポットがONの場合はOFFにして再試行してください。");
            CloseConnection();
        }
        finally
        {
            EndConnection(peer.Session);
        }
    }

    private async void ConnectionListener_ConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
    {
        using var request = args.GetConnectionRequest();

        if (IsConnected)
        {
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: {request.DeviceInformation.Name} からの追加要求を無視しました");
            return;
        }

        if (_isConnecting)
        {
            LogDebug($"[Wi-Fi Direct] 別の接続処理中ですが、{request.DeviceInformation.Name} からの要求を受け付けます。");
        }

        _isConnecting = true;
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: {request.DeviceInformation.Name} から接続要求");

        try
        {
            var tcs = new TaskCompletionSource<WiFiDirectDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var connectionParams = new WiFiDirectConnectionParameters();
                    connectionParams.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);
                    connectionParams.PreferredPairingProcedure = WiFiDirectPairingProcedure.GroupOwnerNegotiation;
                    connectionParams.GroupOwnerIntent = 14;

                    LogDebug($"[Wi-Fi Direct] {request.DeviceInformation.Name} とのペアリング確認中 (受信側)");
                    bool paired = await EnsurePairedAsync(request.DeviceInformation, connectionParams);
                    if (!paired)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }
                    LogDebug("[Wi-Fi Direct] ペアリング済みです (受信側)。IDからWiFiDirectDeviceを作成します。");
                    WiFiDirectDevice? d = null;
                    try
                    {
                        d = await WiFiDirectDevice.FromIdAsync(
                            request.DeviceInformation.Id,
                            connectionParams);
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[Wi-Fi Direct] FromIdAsyncに失敗しました (受信側): {ex.Message} (HResult={ex.HResult:X8})。ペアリング解除後に再試行します...");
                        try
                        {
                            var unpairResult = await request.DeviceInformation.Pairing.UnpairAsync();
                            LogDebug($"[Wi-Fi Direct] ペアリング解除完了: 状態={unpairResult.Status}。クリーンアップのため1500ms待機します...");
                            await Task.Delay(1500);
                        }
                        catch (Exception unpairEx)
                        {
                            LogDebug($"[Wi-Fi Direct] ペアリング解除に失敗しました: {unpairEx.Message}");
                        }

                        paired = await EnsurePairedAsync(request.DeviceInformation, connectionParams);
                        if (paired)
                        {
                            d = await WiFiDirectDevice.FromIdAsync(
                                request.DeviceInformation.Id,
                                connectionParams);
                        }
                    }
                    tcs.TrySetResult(d);
                }
                catch (Exception ex)
                {
                    LogDebug($"[Wi-Fi Direct] 受信側の接続準備に失敗しました: {ex}");
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Wi-Fi Direct受信準備をUIスレッドに登録できませんでした。"));
            }

            var device = await tcs.Task;
            if (device is null)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct受信に失敗しました: デバイスがnullです。");
                return;
            }

            SetWifiDirectDevice(device);
            var endpointPairs = await WaitForEndpointPairsAsync(device, "accept");
            if (endpointPairs.Count == 0)
            {
                return;
            }

            await Task.Delay(1000);

            await EnsureSocketListenerAsync(endpointPairs);

            LogDebug("[Wi-Fi Direct] 接続要求を受け付けました。クライアントを待っています...");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct受信に失敗しました: {ex.Message}。モバイルホットスポットがONの場合はOFFにして再試行してください。");
            CloseConnection();
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void SocketListener_ConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        AttachChannel(args.Socket, "サーバーとして接続", isClient: false);
    }

    private async Task<IReadOnlyList<EndpointPair>> WaitForEndpointPairsAsync(WiFiDirectDevice device, string operation)
    {
        LogDebug($"[Wi-Fi Direct] L2 {operation} が成立しました。IPアドレス割り当てを {DhcpAddressAssignmentDelayMs}ms 待ちます...");
        await Task.Delay(DhcpAddressAssignmentDelayMs);

        var endpointPairs = device.GetConnectionEndpointPairs();
        if (endpointPairs.Count > 0)
        {
            return endpointPairs;
        }

        LogDebug($"[Wi-Fi Direct] {operation} 中にエンドポイントが見つかりません。{DhcpAddressRetryDelayMs}ms後に再試行します...");
        await Task.Delay(DhcpAddressRetryDelayMs);

        endpointPairs = device.GetConnectionEndpointPairs();
        if (endpointPairs.Count == 0)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct {operation} に失敗しました: DHCPのIP割り当てがタイムアウトしました (エンドポイントなし)。");
        }

        return endpointPairs;
    }

    private async Task EnsureSocketListenerAsync(IReadOnlyList<EndpointPair> endpointPairs)
    {
        if (_socketListener is not null)
        {
            return;
        }

        var listener = new StreamSocketListener();
        listener.ConnectionReceived += SocketListener_ConnectionReceived;

        try
        {
            if (endpointPairs.Count > 0)
            {
                await listener.BindEndpointAsync(endpointPairs[0].LocalHostName, ChatPort);
            }
            else
            {
                await listener.BindServiceNameAsync(ChatPort);
            }

            _socketListener = listener;
            LogDebug($"[Wi-Fi Direct] ポート {ChatPort} で待受中");
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: ポート {ChatPort} で待受中");
        }
        catch (Exception ex)
        {
            listener.ConnectionReceived -= SocketListener_ConnectionReceived;
            listener.Dispose();
            LogDebug($"[Wi-Fi Direct] 待受のバインドに失敗しました: {ex.Message}");
        }
    }

    private async Task<bool> TryConnectSocketsAsync(IReadOnlyList<EndpointPair> endpointPairs)
    {
        return await Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= SocketConnectAttempts; attempt++)
            {
                if (_channel != null)
                {
                    return true;
                }

                foreach (var pair in endpointPairs)
                {
                    if (await TryConnectSocketAsync(pair.RemoteHostName, $"クライアントとして接続 (試行 {attempt})"))
                    {
                        return true;
                    }
                }

                foreach (var host in FallbackHosts)
                {
                    if (await TryConnectSocketAsync(host, $"クライアントとして接続 ({host.DisplayName})"))
                    {
                        return true;
                    }
                }

                await Task.Delay(SocketConnectRetryDelayMs);
            }

            LogDebug("[Wi-Fi Direct] ソケット接続の試行回数を使い切りました。");
            return _channel != null;
        });
    }

    private async Task<bool> TryConnectSocketAsync(HostName hostName, string status)
    {
        StreamSocket? socket = null;
        try
        {
            socket = new StreamSocket();
            using var cts = new System.Threading.CancellationTokenSource(SocketConnectTimeoutMs);
            await socket.ConnectAsync(hostName, ChatPort).AsTask(cts.Token);

            var connectedSocket = socket;
            socket = null;
            if (!_dispatcherQueue.TryEnqueue(() => AttachChannel(connectedSocket, status, isClient: true)))
            {
                connectedSocket.Dispose();
                return false;
            }

            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogDebug($"[Wi-Fi Direct] {hostName.DisplayName}:{ChatPort} へのソケット接続に失敗しました: {ex.Message}");
            return false;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private void AttachChannel(StreamSocket socket, string status, bool isClient)
    {
        if (_channel != null)
        {
            socket.Dispose();
            return;
        }

        _channel = new SocketMessageChannel(socket);
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: {status}");
        _ = HandshakeAndReceiveAsync(_channel, isClient);
    }

    private async Task HandshakeAndReceiveAsync(SocketMessageChannel channel, bool isClient)
    {
        try
        {
            var remoteSessionId = await channel.HandshakeAsync(_localSession.SessionId);
            LogDebug($"[ハンドシェイク] 相手={remoteSessionId}");

            var expectedSession = _blePeers.Keys.FirstOrDefault(s => s.SessionId == remoteSessionId);
            if (expectedSession == default)
            {
                StatusChanged?.Invoke(this, $"Wi-Fi Direct: 未確認の相手 {remoteSessionId}; 診断のためソケットを受け付けます");
            }

            StatusChanged?.Invoke(this, $"Wi-Fi Direct: 相手の準備完了 {remoteSessionId}");
            ConnectionStateChanged?.Invoke(this, true);

            while (_channel == channel)
            {
                var message = await channel.ReceiveAsync();
                if (message is null) break;
                MessageReceived?.Invoke(this, message);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"ハンドシェイク/受信に失敗しました: {ex.Message}");
        }
        finally
        {
            if (_channel == channel)
            {
                CloseConnection();
                StatusChanged?.Invoke(this, "Wi-Fi Direct: ソケットを閉じました");
            }
        }
    }

    private async Task ReceiveLoopAsync(SocketMessageChannel channel)
    {
        try
        {
            while (_channel == channel)
            {
                var message = await channel.ReceiveAsync();
                if (message is null)
                {
                    break;
                }

                MessageReceived?.Invoke(this, message);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"受信に失敗しました: {ex.Message}");
        }
        finally
        {
            if (_channel == channel)
            {
                CloseConnection();
                StatusChanged?.Invoke(this, "Wi-Fi Direct: ソケットを閉じました");
            }
        }
    }

    private void SetWifiDirectDevice(WiFiDirectDevice device)
    {
        if (_wifiDirectDevice is not null)
        {
            _wifiDirectDevice.ConnectionStatusChanged -= WifiDirectDevice_ConnectionStatusChanged;
            _wifiDirectDevice.Dispose();
        }

        _wifiDirectDevice = device;
        _wifiDirectDevice.ConnectionStatusChanged += WifiDirectDevice_ConnectionStatusChanged;
    }

    private async Task<bool> EnsurePairedAsync(DeviceInformation deviceInformation, WiFiDirectConnectionParameters connectionParams)
    {
        if (deviceInformation.Pairing.IsPaired)
        {
            return true;
        }

        if (!deviceInformation.Pairing.CanPair)
        {
            LogDebug("[Wi-Fi Direct] CanPairがfalseのため、PairAsyncをスキップします。");
            return true;
        }

        var customPairing = deviceInformation.Pairing.Custom;
        void CustomPairing_PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            LogDebug($"[Wi-Fi Direct] ペアリング要求: 種類={args.PairingKind}");
            using (var deferral = args.GetDeferral())
            {
                if (args.PairingKind == DevicePairingKinds.ProvidePin)
                {
                    args.Accept("0000");
                }
                else
                {
                    args.Accept();
                }
            }
        }

        var devicePairingKinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin;
        DevicePairingResult result;
        var pairingRequestedAttached = false;
        try
        {
            try
            {
                customPairing.PairingRequested += CustomPairing_PairingRequested;
                pairingRequestedAttached = true;
            }
            catch (Exception ex)
            {
                LogDebug($"[Wi-Fi Direct] ペアリング要求ハンドラーを利用できません: {ex.Message}");
            }

            result = await customPairing.PairAsync(devicePairingKinds, DevicePairingProtectionLevel.Default, connectionParams);
        }
        finally
        {
            if (pairingRequestedAttached)
            {
                customPairing.PairingRequested -= CustomPairing_PairingRequested;
            }
        }

        if (result.Status is not DevicePairingResultStatus.Paired and not DevicePairingResultStatus.AlreadyPaired)
        {
            LogDebug($"ペアリングに失敗しました: {result.Status}");
            return false;
        }

        return true;
    }

    private bool ShouldInitiateConnection(ChatSessionPayload remoteSession)
    {
        var comparison = _localSession.SessionId.CompareTo(remoteSession.SessionId);
        if (comparison != 0)
        {
            return comparison < 0;
        }

        return _localSession.Nonce < remoteSession.Nonce;
    }

    private WiFiDirectInformationElement CreateSessionInformationElement()
    {
        return new WiFiDirectInformationElement
        {
            Oui = ChatSessionPayload.WifiDirectOui.AsBuffer(),
            OuiType = ChatSessionPayload.WifiDirectOuiType,
            Value = _localSession.ToBuffer()
        };
    }

    private bool TryGetAdvertisedSession(DeviceInformation deviceInfo, out ChatSessionPayload session)
    {
        session = default;

        try
        {
            foreach (var element in WiFiDirectInformationElement.CreateFromDeviceInformation(deviceInfo))
            {
                if (ChatSessionPayload.IsOurWifiDirectElement(element) &&
                    ChatSessionPayload.TryParse(element.Value, out session))
                {
                    return session != _localSession;
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[Wi-Fi Direct] {deviceInfo.Name} の情報要素を読み取れませんでした: {ex.Message}");
        }

        return false;
    }

    private WifiDirectPeer? TryCreatePeerFromPendingDevices(ChatSessionPayload session)
    {
        foreach (var deviceInfo in _pendingDevicesById.Values.ToArray())
        {
            if (DeviceMatchesSession(deviceInfo, session))
            {
                return AddWifiPeer(deviceInfo, session);
            }
        }

        if (_blePeers.Count == 1 && _pendingDevicesById.Count == 1)
        {
            var deviceInfo = _pendingDevicesById.Values.First();
            LogDebug($"保留中フォールバック: 唯一のBLE相手と唯一のWi-Fi Directデバイスを対応付けます。名前={deviceInfo.Name}");
            return AddWifiPeer(deviceInfo, session);
        }

        return null;
    }

    private WifiDirectPeer AddWifiPeer(DeviceInformation deviceInfo, ChatSessionPayload session)
    {
        _pendingDevicesById.Remove(deviceInfo.Id);

        var peer = new WifiDirectPeer(deviceInfo, session);
        _wifiPeersByDeviceId[deviceInfo.Id] = peer;
        _wifiPeersBySession[session] = peer;
        return peer;
    }

    private bool DeviceNameMatchesSession(string deviceName, ChatSessionPayload session)
    {
        if (!_blePeers.TryGetValue(session, out var blePeer))
        {
            return false;
        }

        return !string.IsNullOrEmpty(blePeer.PcName) &&
            deviceName.Contains(blePeer.PcName, StringComparison.OrdinalIgnoreCase);
    }

    private bool DeviceMatchesSession(DeviceInformation deviceInfo, ChatSessionPayload session)
    {
        if (TryGetAdvertisedSession(deviceInfo, out var advertisedSession))
        {
            return advertisedSession == session;
        }

        return DeviceNameMatchesSession(deviceInfo.Name, session);
    }

    private bool TryMatchBlePeer(DeviceInformation deviceInfo, out ChatSessionPayload session)
    {
        session = default;
        if (TryGetAdvertisedSession(deviceInfo, out var advertisedSession) &&
            _blePeers.ContainsKey(advertisedSession))
        {
            session = advertisedSession;
            return true;
        }

        foreach (var kvp in _blePeers)
        {
            if (DeviceNameMatchesSession(deviceInfo.Name, kvp.Key))
            {
                session = kvp.Key;
                return true;
            }
        }
        return false;
    }

    private async void Watcher_Added(DeviceWatcher sender, DeviceInformation args)
    {
        LogDebug($"監視追加: Id={args.Id} 名前={args.Name}");
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: デバイスを検出: {args.Name}");

        if (TryMatchBlePeer(args, out var session))
        {
            LogDebug($"監視追加: BLE相手と一致しました。セッション={session.SessionId}");
            var peer = AddWifiPeer(args, session);
            PeerDiscovered?.Invoke(this, peer);
            StatusChanged?.Invoke(this, $"Wi-Fi Direct相手: {session.SessionId.ToString()[..8]}");

            await ConnectToPeerInternalAsync(peer, force: false);
        }
        else
        {
            _pendingDevicesById[args.Id] = args;
            LogDebug($"監視追加: まだBLE相手と一致しません。キャッシュします。名前={args.Name}");
        }
    }

    private async void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        LogDebug($"監視更新: Id={args.Id}");
        if (_wifiPeersByDeviceId.TryGetValue(args.Id, out var existingPeer))
        {
            existingPeer.Update(args);
            PeerDiscovered?.Invoke(this, existingPeer);
        }
        else if (_pendingDevicesById.TryGetValue(args.Id, out var cachedInfo))
        {
            try
            {
                var deviceInfo = await DeviceInformation.CreateFromIdAsync(args.Id, DeviceProperties);
                _pendingDevicesById[args.Id] = deviceInfo;
                Debug.WriteLine($"[監視更新の再試行] 名前={deviceInfo.Name}");

                if (TryMatchBlePeer(deviceInfo, out var session))
                {
                    var peer = AddWifiPeer(deviceInfo, session);
                    PeerDiscovered?.Invoke(this, peer);
                    StatusChanged?.Invoke(this, $"Wi-Fi Direct相手: {session.SessionId.ToString()[..8]}");

                    await ConnectToPeerInternalAsync(peer, force: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[監視更新の再試行失敗] {ex.Message}");
            }
        }
    }

    private void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        LogDebug($"監視削除: Id={args.Id}");
        _pendingDevicesById.Remove(args.Id);
        if (_wifiPeersByDeviceId.Remove(args.Id, out var peer))
        {
            _wifiPeersBySession.Remove(peer.Session);
            StatusChanged?.Invoke(this, $"Wi-Fi Direct相手を削除: {peer.Session.SessionId}");
        }
    }

    private void Watcher_EnumerationCompleted(DeviceWatcher sender, object args)
    {
        LogDebug("監視: 初回スキャンが完了しました。");
        StatusChanged?.Invoke(this, "Wi-Fi Direct: 列挙完了");
    }

    private void Watcher_Stopped(DeviceWatcher sender, object args)
    {
        LogDebug("監視停止");
        StatusChanged?.Invoke(this, "Wi-Fi Direct: 監視停止");
    }

    private void Publisher_StatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        LogDebug($"広告状態変更: {ToJapaneseAdvertisementStatus(args.Status)}");
        StatusChanged?.Invoke(this, $"Wi-Fi Direct広告: {ToJapaneseAdvertisementStatus(args.Status)}");

        if (args.Error != WiFiDirectError.Success)
        {
            LogDebug($"広告状態変更: エラー {ToJapaneseWifiDirectError(args.Error)}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct広告エラー: {ToJapaneseWifiDirectError(args.Error)}。モバイルホットスポットがONの場合はOFFにして再試行してください。");
        }
    }

    private void WifiDirectDevice_ConnectionStatusChanged(WiFiDirectDevice sender, object args)
    {
        StatusChanged?.Invoke(this, $"Wi-Fi Direct L2: {ToJapaneseConnectionStatus(sender.ConnectionStatus)}");

        if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected)
        {
            CloseConnection();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WifiDirectChatService));
        }
    }

    private static string ToJapaneseAdvertisementStatus(WiFiDirectAdvertisementPublisherStatus status) => status switch
    {
        WiFiDirectAdvertisementPublisherStatus.Created => "作成済み",
        WiFiDirectAdvertisementPublisherStatus.Started => "開始済み",
        WiFiDirectAdvertisementPublisherStatus.Stopped => "停止中",
        WiFiDirectAdvertisementPublisherStatus.Aborted => "中断",
        _ => status.ToString()
    };

    private static string ToJapaneseWifiDirectError(WiFiDirectError error) => error switch
    {
        WiFiDirectError.Success => "成功",
        WiFiDirectError.RadioNotAvailable => "Wi-Fi Direct無線を利用できません",
        WiFiDirectError.ResourceInUse => "リソースが使用中です",
        _ => error.ToString()
    };

    private static string ToJapaneseConnectionStatus(WiFiDirectConnectionStatus status) => status switch
    {
        WiFiDirectConnectionStatus.Connected => "接続済み",
        WiFiDirectConnectionStatus.Disconnected => "切断済み",
        _ => status.ToString()
    };
}
