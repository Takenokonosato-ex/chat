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

namespace chat;

public sealed class WifiDirectChatService : IDisposable
{
    private const string ChatPort = "50001";
    private static readonly string[] DeviceProperties =
    {
        "System.Devices.WiFiDirect.InformationElements"
    };

    private readonly ChatSessionPayload _localSession;
    private readonly Dictionary<string, WifiDirectPeer> _wifiPeersByDeviceId = new();
    private readonly Dictionary<ChatSessionPayload, WifiDirectPeer> _wifiPeersBySession = new();
    private readonly HashSet<ChatSessionPayload> _blePeers = new();
    private WiFiDirectAdvertisementPublisher? _publisher;
    private WiFiDirectConnectionListener? _connectionListener;
    private DeviceWatcher? _watcher;
    private WiFiDirectDevice? _wifiDirectDevice;
    private StreamSocketListener? _socketListener;
    private SocketMessageChannel? _channel;
    private bool _isStarted;
    private bool _isConnecting;
    private bool _disposed;

    public WifiDirectChatService(ChatSessionPayload localSession)
    {
        _localSession = localSession;
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<WifiDirectPeer>? PeerDiscovered;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsStarted => _isStarted;

    public bool IsConnected => _channel is not null;

    public async Task StartAsync()
    {
        ThrowIfDisposed();

        if (_isStarted)
        {
            return;
        }

        _publisher = new WiFiDirectAdvertisementPublisher();
        _publisher.StatusChanged += Publisher_StatusChanged;
        _publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Intensive;
        _publisher.Advertisement.InformationElements.Add(_localSession.ToWifiDirectInformationElement());

        try
        {
            _connectionListener = new WiFiDirectConnectionListener();
            _connectionListener.ConnectionRequested += ConnectionListener_ConnectionRequested;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct listener start failed: {ex.Message}");
            Stop();
            return;
        }

        var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
        _watcher = DeviceInformation.CreateWatcher(selector, DeviceProperties);
        _watcher.Added += Watcher_Added;
        _watcher.Updated += Watcher_Updated;
        _watcher.Removed += Watcher_Removed;
        _watcher.EnumerationCompleted += Watcher_EnumerationCompleted;
        _watcher.Stopped += Watcher_Stopped;

        try
        {
            _publisher.Start();
            _watcher.Start();
            _isStarted = true;
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: Started ({_publisher.Status})");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct start failed: {ex.Message}");
            Stop();
        }

        await Task.CompletedTask;
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
            try
            {
                _publisher.Stop();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Wi-Fi Direct publisher stop failed: {ex.Message}");
            }

            _publisher = null;
        }

        _wifiPeersByDeviceId.Clear();
        _wifiPeersBySession.Clear();
        _blePeers.Clear();
        _isStarted = false;
        _isConnecting = false;
        StatusChanged?.Invoke(this, "Wi-Fi Direct: Stopped");
    }

    public async Task AddBlePeerAsync(BlePeer peer)
    {
        var session = new ChatSessionPayload(peer.SessionId, peer.Nonce);
        _blePeers.Add(session);

        if (_wifiPeersBySession.TryGetValue(session, out var wifiPeer))
        {
            await TryAutoConnectAsync(wifiPeer);
        }
    }

    public async Task ConnectToPeerAsync(WifiDirectPeer peer)
    {
        await ConnectToPeerInternalAsync(peer, force: true);
    }

    public async Task SendMessageAsync(string message)
    {
        if (_channel is null)
        {
            ErrorOccurred?.Invoke(this, "未接続です。Wi-Fi Direct 接続後に送信してください。");
            return;
        }

        try
        {
            await _channel.SendAsync(message);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Message send failed: {ex.Message}");
            CloseConnection();
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

    private async Task TryAutoConnectAsync(WifiDirectPeer peer)
    {
        if (!ShouldInitiateConnection(peer.Session))
        {
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: waiting for peer {peer.Session.SessionId} to connect");
            return;
        }

        await ConnectToPeerInternalAsync(peer, force: false);
    }

    private async Task ConnectToPeerInternalAsync(WifiDirectPeer peer, bool force)
    {
        if (IsConnected || _isConnecting)
        {
            return;
        }

        if (!force && !ShouldInitiateConnection(peer.Session))
        {
            return;
        }

        _isConnecting = true;
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: connecting to {peer.DeviceInformation.Name}");

        try
        {
            await EnsurePairedAsync(peer.DeviceInformation);
            var device = await WiFiDirectDevice.FromIdAsync(peer.DeviceInformation.Id);
            if (device is null)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct connection failed: device was null.");
                return;
            }

            SetWifiDirectDevice(device);
            var endpointPairs = device.GetConnectionEndpointPairs();
            if (endpointPairs.Count == 0)
            {
                ErrorOccurred?.Invoke(this, "Wi-Fi Direct connection failed: no endpoint pairs.");
                return;
            }

            await Task.Delay(2000);

            var socket = new StreamSocket();
            await socket.ConnectAsync(endpointPairs[0].RemoteHostName, ChatPort);
            AttachChannel(socket, "Connected as client");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Wi-Fi Direct connect failed: {ex.Message} Mobile Hotspot が ON の場合は OFF にしてください。");
            CloseConnection();
        }
        finally
        {
            _isConnecting = false;
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
            await EnsurePairedAsync(request.DeviceInformation);
            var device = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
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

            _socketListener = new StreamSocketListener();
            _socketListener.ConnectionReceived += SocketListener_ConnectionReceived;
            await _socketListener.BindEndpointAsync(endpointPairs[0].LocalHostName, ChatPort);
            StatusChanged?.Invoke(this, $"Wi-Fi Direct: listening on {endpointPairs[0].LocalHostName}:{ChatPort}");
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
        AttachChannel(args.Socket, "Connected as server");
    }

    private void AttachChannel(StreamSocket socket, string status)
    {
        _channel?.Dispose();
        _channel = new SocketMessageChannel(socket);
        StatusChanged?.Invoke(this, $"Wi-Fi Direct: {status}");
        ConnectionStateChanged?.Invoke(this, true);
        _ = ReceiveLoopAsync(_channel);
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

    private async Task EnsurePairedAsync(DeviceInformation deviceInformation)
    {
        if (deviceInformation.Pairing.IsPaired)
        {
            return;
        }

        var result = await deviceInformation.Pairing.PairAsync();
        if (result.Status is not DevicePairingResultStatus.Paired and not DevicePairingResultStatus.AlreadyPaired)
        {
            throw new InvalidOperationException($"Pairing failed: {result.Status}");
        }
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

    private void Watcher_Added(DeviceWatcher sender, DeviceInformation args)
    {
        TryAddOrUpdateWifiPeer(args);
    }

    private async void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (_wifiPeersByDeviceId.TryGetValue(args.Id, out var peer))
        {
            peer.Update(args);
            PeerDiscovered?.Invoke(this, peer);
        }
        else
        {
            try
            {
                var deviceInfo = await DeviceInformation.CreateFromIdAsync(
                    args.Id,
                    DeviceProperties);
                TryAddOrUpdateWifiPeer(deviceInfo);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Wi-Fi Direct update failed: {ex.Message}");
            }
        }
    }

    private void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (_wifiPeersByDeviceId.Remove(args.Id, out var peer))
        {
            _wifiPeersBySession.Remove(peer.Session);
            StatusChanged?.Invoke(this, $"Wi-Fi Direct peer removed: {peer.Session.SessionId}");
        }
    }

    private void Watcher_EnumerationCompleted(DeviceWatcher sender, object args)
    {
        StatusChanged?.Invoke(this, "Wi-Fi Direct: enumeration completed");
    }

    private void Watcher_Stopped(DeviceWatcher sender, object args)
    {
        StatusChanged?.Invoke(this, "Wi-Fi Direct: watcher stopped");
    }

    private async void TryAddOrUpdateWifiPeer(DeviceInformation deviceInformation)
    {
        if (!TryGetChatSession(deviceInformation, out var session))
        {
            return;
        }

        if (session == _localSession)
        {
            return;
        }

        var peer = new WifiDirectPeer(deviceInformation, session);
        _wifiPeersByDeviceId[deviceInformation.Id] = peer;
        _wifiPeersBySession[session] = peer;
        PeerDiscovered?.Invoke(this, peer);
        StatusChanged?.Invoke(this, $"Wi-Fi Direct peer found: {session.SessionId}");

        if (_blePeers.Contains(session))
        {
            await TryAutoConnectAsync(peer);
        }
    }

    private static bool TryGetChatSession(
        DeviceInformation deviceInformation,
        out ChatSessionPayload session)
    {
        session = default;

        try
        {
            var elements =
                WiFiDirectInformationElement
                    .CreateFromDeviceInformation(deviceInformation);

            Debug.WriteLine(
                $"Device={deviceInformation.Name} Elements={elements.Count}");
            foreach (var element in elements)
            {
                var oui = WindowsRuntimeBufferExtensions.ToArray(element.Oui);

                Debug.WriteLine(
                    $"Type={element.OuiType} " +
                    $"OUI={BitConverter.ToString(oui)}");
                if (!ChatSessionPayload.IsOurWifiDirectElement(element))
                {
                    continue;
                }

                if (ChatSessionPayload.TryParse(element.Value, out session))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        return false;
    }

    private void Publisher_StatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        StatusChanged?.Invoke(this, $"Wi-Fi Direct publisher: {args.Status}");

        if (args.Error != WiFiDirectError.Success)
        {
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
