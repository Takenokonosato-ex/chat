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
    private const int SocketConnectTimeoutMs = 3000;
    private const int SocketConnectRetryDelayMs = 1500;
    private const int PublisherStartTimeoutMs = 3000;
    private const int ConnectionRetryCooldownSeconds = 10;
    private static readonly string[] DeviceProperties = Array.Empty<string>();
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
        MessageReceived?.Invoke(this, $"[System] {msg}");
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
            LogDebug("StartAsync: Creating WiFiDirectConnectionListener...");
            _connectionListener = new WiFiDirectConnectionListener();
            _connectionListener.ConnectionRequested += ConnectionListener_ConnectionRequested;

            LogDebug("StartAsync: Creating DeviceWatcher...");
            var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
            LogDebug($"StartAsync: DeviceSelector is '{selector}'");
            _watcher = DeviceInformation.CreateWatcher(selector, DeviceProperties);
            _watcher.Added += Watcher_Added;
            _watcher.Updated += Watcher_Updated;
            _watcher.Removed += Watcher_Removed;
            _watcher.EnumerationCompleted += Watcher_EnumerationCompleted;
            _watcher.Stopped += Watcher_Stopped;

            LogDebug("StartAsync: Starting watcher...");
            _watcher.Start();

            _isStarted = true;
            LogDebug("StartAsync: Started successfully.");
            StatusChanged?.Invoke(this, "Wi-Fi Direct: Started");

            _ = StartPublisherBestEffortAsync();
        }
        catch (Exception ex)
        {
            LogDebug($"StartAsync: Start failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct start failed: {ex.Message}");
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

        StatusChanged?.Invoke(this, "Wi-Fi Direct: publisher starting");

        var startTask = Task.Run(() =>
        {
            LogDebug("Publisher: Initializing Wi-Fi Direct publisher...");
            var publisher = new WiFiDirectAdvertisementPublisher();
            publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;

            LogDebug("Publisher: Starting...");
            publisher.Start();
            return publisher;
        });

        var completedTask = await Task.WhenAny(startTask, Task.Delay(PublisherStartTimeoutMs));
        if (completedTask != startTask)
        {
            LogDebug("Publisher: start timed out; continuing without blocking the app.");
            StatusChanged?.Invoke(this, "Wi-Fi Direct: publisher start timed out");
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
            LogDebug($"Publisher: start failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct publisher start failed: {ex.Message}");
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
            LogDebug($"Publisher: delayed start failed: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct publisher start failed: {ex.Message}");
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
        LogDebug($"Publisher: Started with status {_publisher.Status}");
        StatusChanged?.Invoke(this, $"Wi-Fi Direct publisher: {_publisher.Status}");
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
                ErrorOccurred?.Invoke(this, $"Wi-Fi Direct watcher stop failed: {ex.Message}");
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
        StatusChanged?.Invoke(this, "Wi-Fi Direct: Stopped");
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
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct publisher stop failed: {ex.Message}");
        }
    }

    public async Task AddBlePeerAsync(BlePeer peer)
    {
        var session = new ChatSessionPayload(peer.SessionId, peer.Nonce);
        _blePeers[session] = peer;

        // BLEで発見したセッションに一致するWi-Fi Directピアがいれば自動接続
        if (_wifiPeersBySession.TryGetValue(session, out var wifiPeer))
        {
            await ConnectToPeerInternalAsync(wifiPeer, force: false);
        }
        else
        {
            // すでにWatcherで発見済みの保留中Wi-Fi Directデバイスから探す
            var newPeer = TryCreatePeerFromPendingDevices(session);
            if (newPeer != null)
            {
                PeerDiscovered?.Invoke(this, newPeer);
                StatusChanged?.Invoke(this, $"Wi-Fi Direct peer: {session.SessionId.ToString()[..8]}");
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
            ErrorOccurred?.Invoke(this, "未接続です。Wi-Fi Direct 接続後に送信してください。");
            return false;
        }

        try
        {
            await _channel.SendAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Message send failed: {ex.Message}");
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

        if (!force && !ShouldInitiateConnection(peer.Session))
        {
            return;
        }

        if (!TryBeginConnection(peer.Session, force))
        {
            LogDebug($"[Wi-Fi Direct] Skipping duplicate connection attempt to {peer.DeviceInformation.Name}");
            return;
        }

        StatusChanged?.Invoke(this, $"Wi-Fi Direct: connecting to {peer.DeviceInformation.Name}");

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

                    LogDebug($"[Wi-Fi Direct] EnsurePairedAsync to {peer.DeviceInformation.Name}");
                    bool paired = await EnsurePairedAsync(peer.DeviceInformation, connectionParams);
                    if (!paired)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }
                    LogDebug($"[Wi-Fi Direct] Paired. Creating WiFiDirectDevice from ID.");
                    WiFiDirectDevice? d = null;
                    try
                    {
                        d = await WiFiDirectDevice.FromIdAsync(peer.DeviceInformation.Id);
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[Wi-Fi Direct] FromIdAsync failed: {ex.Message} (HResult={ex.HResult:X8}). Trying to unpair and retry pairing...");
                        try
                        {
                            await peer.DeviceInformation.Pairing.UnpairAsync();
                        }
                        catch (Exception unpairEx)
                        {
                            LogDebug($"[Wi-Fi Direct] UnpairAsync failed: {unpairEx.Message}");
                        }

                        paired = await EnsurePairedAsync(peer.DeviceInformation, connectionParams);
                        if (paired)
                        {
                            d = await WiFiDirectDevice.FromIdAsync(peer.DeviceInformation.Id);
                        }
                    }
                    tcs.TrySetResult(d);
                }
                catch (Exception ex)
                {
                    LogDebug($"[Wi-Fi Direct] Connection setup failed: {ex}");
                    ErrorOccurred?.Invoke(this, $"EnsurePairedエラー: {ex.Message}");
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue Wi-Fi Direct connection setup on the UI thread."));
            }

            var device = await tcs.Task;
            if (device is null)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct connection failed: device was null.");
                return;
            }

            SetWifiDirectDevice(device);
            var endpointPairs = device.GetConnectionEndpointPairs();
            if (endpointPairs.Count == 0)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct connection failed: no endpoint pairs. L2 connection might have failed.");
                return;
            }

            LogDebug($"[Wi-Fi Direct] Endpoints found. Connecting to {endpointPairs[0].RemoteHostName}:{ChatPort}");
            await Task.Delay(1000);

            await EnsureSocketListenerAsync();

            var socketConnected = await TryConnectSocketsAsync(endpointPairs);
            if (!socketConnected)
            {
                StatusChanged?.Invoke(this, "Wi-Fi Direct: waiting for peer socket");
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct connect failed: {ex.Message} Mobile Hotspot が ON の場合は OFF にしてください。");
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

        if (IsConnected || _isConnecting)
        {
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: ignored extra request from {request.DeviceInformation.Name}");
            return;
        }

        _isConnecting = true;
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: request from {request.DeviceInformation.Name}");

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

                    LogDebug($"[Wi-Fi Direct] EnsurePairedAsync (Listener) to {request.DeviceInformation.Name}");
                    bool paired = await EnsurePairedAsync(request.DeviceInformation, connectionParams);
                    if (!paired)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }
                    LogDebug($"[Wi-Fi Direct] Paired (Listener). Creating WiFiDirectDevice from ID.");
                    WiFiDirectDevice? d = null;
                    try
                    {
                        d = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[Wi-Fi Direct] FromIdAsync (Listener) failed: {ex.Message} (HResult={ex.HResult:X8}). Trying to unpair and retry pairing...");
                        try
                        {
                            await request.DeviceInformation.Pairing.UnpairAsync();
                        }
                        catch (Exception unpairEx)
                        {
                            LogDebug($"[Wi-Fi Direct] UnpairAsync failed: {unpairEx.Message}");
                        }

                        paired = await EnsurePairedAsync(request.DeviceInformation, connectionParams);
                        if (paired)
                        {
                            d = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
                        }
                    }
                    tcs.TrySetResult(d);
                }
                catch (Exception ex)
                {
                    LogDebug($"[Wi-Fi Direct] Listener setup failed: {ex}");
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Failed to enqueue Wi-Fi Direct accept setup on the UI thread."));
            }

            var device = await tcs.Task;
            if (device is null)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct accept failed: device was null.");
                return;
            }

            SetWifiDirectDevice(device);
            var endpointPairs = device.GetConnectionEndpointPairs();
            if (endpointPairs.Count == 0)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct accept failed: no endpoint pairs.");
                return;
            }

            await Task.Delay(1000);

            await EnsureSocketListenerAsync();

            var socketConnected = await TryConnectSocketsAsync(endpointPairs);
            if (!socketConnected)
            {
                StatusChanged?.Invoke(this, "Wi-Fi Direct: waiting for peer socket");
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct accept failed: {ex.Message} Mobile Hotspot が ON の場合は OFF にしてください。");
            CloseConnection();
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void SocketListener_ConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        AttachChannel(args.Socket, "Connected as server", isClient: false);
    }

    private async Task EnsureSocketListenerAsync()
    {
        if (_socketListener is not null)
        {
            return;
        }

        var listener = new StreamSocketListener();
        listener.ConnectionReceived += SocketListener_ConnectionReceived;

        try
        {
            await listener.BindServiceNameAsync(ChatPort);
            _socketListener = listener;
            LogDebug($"[Wi-Fi Direct] Listening on port {ChatPort}");
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: listening on port {ChatPort}");
        }
        catch (Exception ex)
        {
            listener.ConnectionReceived -= SocketListener_ConnectionReceived;
            listener.Dispose();
            LogDebug($"[Wi-Fi Direct] Failed to bind listener: {ex.Message}");
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
                    if (await TryConnectSocketAsync(pair.RemoteHostName, $"Connected as client (attempt {attempt})"))
                    {
                        return true;
                    }
                }

                foreach (var host in FallbackHosts)
                {
                    if (await TryConnectSocketAsync(host, $"Connected as client ({host.DisplayName})"))
                    {
                        return true;
                    }
                }

                await Task.Delay(SocketConnectRetryDelayMs);
            }

            LogDebug("[Wi-Fi Direct] Socket connect attempts exhausted.");
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
            LogDebug($"[Wi-Fi Direct] Socket connect to {hostName.DisplayName}:{ChatPort} failed: {ex.Message}");
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
            LogDebug($"[Handshake] remote={remoteSessionId}");

            var expectedSession = _blePeers.Keys.FirstOrDefault(s => s.SessionId == remoteSessionId);
            if (expectedSession == default)
            {
                StatusChanged?.Invoke(this, $"Wi-Fi Direct: unknown peer {remoteSessionId}, disconnecting");
                CloseConnection();
                return;
            }

            StatusChanged?.Invoke(this, $"Wi-Fi Direct: verified peer {remoteSessionId}");
            ConnectionStateChanged?.Invoke(this, true);

            // 通常の受信ループへ
            while (_channel == channel)
            {
                var message = await channel.ReceiveAsync();
                if (message is null) break;
                MessageReceived?.Invoke(this, message);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Handshake/Receive failed: {ex.Message}");
        }
        finally
        {
            if (_channel == channel)
            {
                CloseConnection();
                StatusChanged?.Invoke(this, "Wi-Fi Direct: socket closed");
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
            ErrorOccurred?.Invoke(this, $"Receive failed: {ex.Message}");
        }
        finally
        {
            if (_channel == channel)
            {
                CloseConnection();
                StatusChanged?.Invoke(this, "Wi-Fi Direct: socket closed");
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

        var customPairing = deviceInformation.Pairing.Custom;
        void CustomPairing_PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            LogDebug($"[Wi-Fi Direct] PairingRequested: Kind={args.PairingKind}");
            using (var deferral = args.GetDeferral())
            {
                if (args.PairingKind == DevicePairingKinds.ProvidePin)
                {
                    args.Accept("0000"); // 一部のドライバではPINが必要。固定PIN
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
                LogDebug($"[Wi-Fi Direct] PairingRequested handler unavailable: {ex.Message}");
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
            LogDebug($"Pairing failed: {result.Status}");
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

    private WifiDirectPeer? TryCreatePeerFromPendingDevices(ChatSessionPayload session)
    {
        foreach (var deviceInfo in _pendingDevicesById.Values.ToArray())
        {
            if (DeviceNameMatchesSession(deviceInfo.Name, session))
            {
                return AddWifiPeer(deviceInfo, session);
            }
        }

        if (_blePeers.Count == 1 && _pendingDevicesById.Count == 1)
        {
            var deviceInfo = _pendingDevicesById.Values.First();
            LogDebug($"Pending fallback: pairing the only BLE peer with the only Wi-Fi Direct device. Name={deviceInfo.Name}");
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

    private bool TryMatchBlePeer(string deviceName, out ChatSessionPayload session)
    {
        session = default;
        foreach (var kvp in _blePeers)
        {
            if (DeviceNameMatchesSession(deviceName, kvp.Key))
            {
                session = kvp.Key;
                return true;
            }
        }
        return false;
    }

    private async void Watcher_Added(DeviceWatcher sender, DeviceInformation args)
    {
        LogDebug($"Watcher_Added: Id={args.Id} Name={args.Name}");
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: 見つけたデバイス: {args.Name}");

        if (TryMatchBlePeer(args.Name, out var session))
        {
            LogDebug($"Watcher_Added: Matched BLE peer! Session={session.SessionId}");
            var peer = AddWifiPeer(args, session);
            PeerDiscovered?.Invoke(this, peer);
            StatusChanged?.Invoke(this, $"Wi-Fi Direct peer: {session.SessionId.ToString()[..8]}");

            await ConnectToPeerInternalAsync(peer, force: false);
        }
        else
        {
            // BLEでまだ発見されていない場合はキャッシュに追加してUpdatedで再試行
            _pendingDevicesById[args.Id] = args;
            LogDebug($"Watcher_Added: No BLE match yet. Cached. Name={args.Name}");
        }
    }

    private async void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        LogDebug($"Watcher_Updated: Id={args.Id}");
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
                Debug.WriteLine($"[Watcher_Updated retry] Name={deviceInfo.Name}");

                if (TryMatchBlePeer(deviceInfo.Name, out var session))
                {
                    var peer = AddWifiPeer(deviceInfo, session);
                    PeerDiscovered?.Invoke(this, peer);
                    StatusChanged?.Invoke(this, $"Wi-Fi Direct peer: {session.SessionId.ToString()[..8]}");

                    await ConnectToPeerInternalAsync(peer, force: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Watcher_Updated retry failed] {ex.Message}");
            }
        }
    }

    private void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        LogDebug($"Watcher_Removed: Id={args.Id}");
        _pendingDevicesById.Remove(args.Id);
        if (_wifiPeersByDeviceId.Remove(args.Id, out var peer))
        {
            _wifiPeersBySession.Remove(peer.Session);
            StatusChanged?.Invoke(this, $"Wi-Fi Direct peer removed: {peer.Session.SessionId}");
        }
    }

    private void Watcher_EnumerationCompleted(DeviceWatcher sender, object args)
    {
        LogDebug("Watcher_EnumerationCompleted: Finished initial scan.");
        StatusChanged?.Invoke(this, "Wi-Fi Direct: enumeration completed");
    }

    private void Watcher_Stopped(DeviceWatcher sender, object args)
    {
        LogDebug("Watcher_Stopped");
        StatusChanged?.Invoke(this, "Wi-Fi Direct: watcher stopped");
    }

    private void Publisher_StatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        LogDebug($"Publisher_StatusChanged: {args.Status}");
        StatusChanged?.Invoke(this, $"Wi-Fi Direct publisher: {args.Status}");

        if (args.Error != WiFiDirectError.Success)
        {
            LogDebug($"Publisher_StatusChanged: ERROR {args.Error}");
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct publisher error: {args.Error}. Mobile Hotspot が ON の場合は OFF にしてください。");
        }
    }

    private void WifiDirectDevice_ConnectionStatusChanged(WiFiDirectDevice sender, object args)
    {
        StatusChanged?.Invoke(this, $"Wi-Fi Direct L2: {sender.ConnectionStatus}");

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
}
