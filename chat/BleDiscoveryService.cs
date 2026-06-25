using System;
using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace chat;

public sealed class BleDiscoveryService : IDisposable
{
    private readonly ChatSessionPayload _localSession = ChatSessionPayload.CreateLocal();
    private BluetoothLEAdvertisementPublisher? _publisher;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<string>? PublisherStatusChanged;
    public event EventHandler<string>? WatcherStatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<BlePeer>? PeerDiscovered;

    public ChatSessionPayload LocalSession => _localSession;

    public Guid SessionId => _localSession.SessionId;

    public uint Nonce => _localSession.Nonce;

    public bool IsAdvertising =>
        _publisher?.Status is BluetoothLEAdvertisementPublisherStatus.Started or
            BluetoothLEAdvertisementPublisherStatus.Waiting;

    public bool IsScanning =>
        _watcher?.Status is BluetoothLEAdvertisementWatcherStatus.Started or
            BluetoothLEAdvertisementWatcherStatus.Created;

    public void StartAdvertising()
    {
        ThrowIfDisposed();

        if (IsAdvertising)
        {
            return;
        }

        StopAdvertising();

        var advertisement = new BluetoothLEAdvertisement();
        advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData
        {
            CompanyId = ChatSessionPayload.BleCompanyId,
            Data = _localSession.ToBuffer()
        });

        _publisher = new BluetoothLEAdvertisementPublisher(advertisement);
        _publisher.StatusChanged += Publisher_StatusChanged;

        try
        {
            _publisher.Start();
            PublisherStatusChanged?.Invoke(this, $"Advertising: {_publisher.Status}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"BLE advertise start failed: {ex.Message}");
            StopAdvertising();
        }
    }

    public void StopAdvertising()
    {
        if (_publisher is null)
        {
            return;
        }

        _publisher.StatusChanged -= Publisher_StatusChanged;

        try
        {
            if (_publisher.Status != BluetoothLEAdvertisementPublisherStatus.Stopped)
            {
                _publisher.Stop();
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"BLE advertise stop failed: {ex.Message}");
        }
        finally
        {
            _publisher = null;
            PublisherStatusChanged?.Invoke(this, "Advertising: Stopped");
        }
    }

    public void StartScanning()
    {
        ThrowIfDisposed();

        if (IsScanning)
        {
            return;
        }

        StopScanning();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += Watcher_Received;
        _watcher.Stopped += Watcher_Stopped;

        try
        {
            _watcher.Start();
            WatcherStatusChanged?.Invoke(this, $"Scanning: {_watcher.Status}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"BLE scan start failed: {ex.Message}");
            StopScanning();
        }
    }

    public void StopScanning()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.Received -= Watcher_Received;
        _watcher.Stopped -= Watcher_Stopped;

        try
        {
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Stopped)
            {
                _watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"BLE scan stop failed: {ex.Message}");
        }
        finally
        {
            _watcher = null;
            WatcherStatusChanged?.Invoke(this, "Scanning: Stopped");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopScanning();
        StopAdvertising();
        _disposed = true;
    }

    private void Publisher_StatusChanged(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
    {
        PublisherStatusChanged?.Invoke(this, $"Advertising: {args.Status}");

        if (args.Error != BluetoothError.Success)
        {
            ErrorOccurred?.Invoke(this, $"BLE advertise status error: {args.Error}");
        }
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        WatcherStatusChanged?.Invoke(this, $"Scanning: Stopped ({args.Error})");

        if (args.Error != BluetoothError.Success)
        {
            ErrorOccurred?.Invoke(this, $"BLE scan stopped with error: {args.Error}");
        }
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var manufacturerData = args.Advertisement.ManufacturerData
            .FirstOrDefault(data => data.CompanyId == ChatSessionPayload.BleCompanyId);

        if (manufacturerData is null || !ChatSessionPayload.TryParse(manufacturerData.Data, out var session))
        {
            return;
        }

        if (session == _localSession)
        {
            return;
        }

        PeerDiscovered?.Invoke(
            this,
            new BlePeer(args.BluetoothAddress, session.SessionId, session.Nonce, args.RawSignalStrengthInDBm, args.Timestamp));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BleDiscoveryService));
        }
    }
}
