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
            PublisherStatusChanged?.Invoke(this, $"広告: {ToJapaneseStatus(_publisher.Status)}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"BLE広告の開始に失敗しました: {ex.Message}");
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
            ErrorOccurred?.Invoke(this, $"BLE広告の停止に失敗しました: {ex.Message}");
        }
        finally
        {
            _publisher = null;
            PublisherStatusChanged?.Invoke(this, "広告: 停止");
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
            WatcherStatusChanged?.Invoke(this, $"スキャン: {ToJapaneseStatus(_watcher.Status)}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"BLEスキャンの開始に失敗しました: {ex.Message}");
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
            ErrorOccurred?.Invoke(this, $"BLEスキャンの停止に失敗しました: {ex.Message}");
        }
        finally
        {
            _watcher = null;
            WatcherStatusChanged?.Invoke(this, "スキャン: 停止");
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
        PublisherStatusChanged?.Invoke(this, $"広告: {ToJapaneseStatus(args.Status)}");

        if (args.Error != BluetoothError.Success)
        {
            ErrorOccurred?.Invoke(this, $"BLE広告の状態エラー: {ToJapaneseError(args.Error)}");
        }
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        WatcherStatusChanged?.Invoke(this, $"スキャン: 停止 ({ToJapaneseError(args.Error)})");

        if (args.Error != BluetoothError.Success)
        {
            ErrorOccurred?.Invoke(this, $"BLEスキャンがエラーで停止しました: {ToJapaneseError(args.Error)}");
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

    private static string ToJapaneseStatus(BluetoothLEAdvertisementPublisherStatus status) =>
        status switch
        {
            BluetoothLEAdvertisementPublisherStatus.Created => "作成済み",
            BluetoothLEAdvertisementPublisherStatus.Waiting => "待機中",
            BluetoothLEAdvertisementPublisherStatus.Started => "開始",
            BluetoothLEAdvertisementPublisherStatus.Stopping => "停止中",
            BluetoothLEAdvertisementPublisherStatus.Stopped => "停止",
            BluetoothLEAdvertisementPublisherStatus.Aborted => "中断",
            _ => status.ToString()
        };

    private static string ToJapaneseStatus(BluetoothLEAdvertisementWatcherStatus status) =>
        status switch
        {
            BluetoothLEAdvertisementWatcherStatus.Created => "作成済み",
            BluetoothLEAdvertisementWatcherStatus.Started => "開始",
            BluetoothLEAdvertisementWatcherStatus.Stopping => "停止中",
            BluetoothLEAdvertisementWatcherStatus.Stopped => "停止",
            BluetoothLEAdvertisementWatcherStatus.Aborted => "中断",
            _ => status.ToString()
        };

    private static string ToJapaneseError(BluetoothError error) =>
        error switch
        {
            BluetoothError.Success => "正常",
            BluetoothError.RadioNotAvailable => "Bluetooth無線を利用できません",
            BluetoothError.ResourceInUse => "リソースが使用中です",
            BluetoothError.DeviceNotConnected => "デバイスが接続されていません",
            BluetoothError.DisabledByPolicy => "ポリシーにより無効です",
            BluetoothError.DisabledByUser => "ユーザーにより無効です",
            BluetoothError.NotSupported => "サポートされていません",
            BluetoothError.TransportNotSupported => "転送方式がサポートされていません",
            BluetoothError.OtherError => "その他のエラー",
            _ => error.ToString()
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BleDiscoveryService));
        }
    }
}
