using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace chat;

public sealed class BleDiscoveryService : IDisposable
{
    private const ushort PrototypeCompanyId = 0xFFFF;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CHAT");

    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly uint _nonce = CreateNonce();
    private BluetoothLEAdvertisementPublisher? _publisher;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<string>? PublisherStatusChanged;
    public event EventHandler<string>? WatcherStatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<BlePeer>? PeerDiscovered;

    public Guid SessionId => _sessionId;

    public uint Nonce => _nonce;

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
            CompanyId = PrototypeCompanyId,
            Data = BuildPayload()
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

    private IBuffer BuildPayload()
    {
        using var writer = new DataWriter();
        writer.WriteBytes(Magic);
        writer.WriteByte(1);
        writer.WriteBytes(_sessionId.ToByteArray());
        writer.WriteUInt32(_nonce);
        return writer.DetachBuffer();
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
            .FirstOrDefault(data => data.CompanyId == PrototypeCompanyId);

        if (manufacturerData is null || !TryParsePayload(manufacturerData.Data, out var sessionId, out var nonce))
        {
            return;
        }

        if (sessionId == _sessionId && nonce == _nonce)
        {
            return;
        }

        PeerDiscovered?.Invoke(
            this,
            new BlePeer(args.BluetoothAddress, sessionId, nonce, args.RawSignalStrengthInDBm, args.Timestamp));
    }

    private static bool TryParsePayload(IBuffer payload, out Guid sessionId, out uint nonce)
    {
        sessionId = Guid.Empty;
        nonce = 0;

        if (payload.Length < Magic.Length + 1 + 16 + sizeof(uint))
        {
            return false;
        }

        var reader = DataReader.FromBuffer(payload);
        var magic = new byte[Magic.Length];
        reader.ReadBytes(magic);

        if (!magic.SequenceEqual(Magic))
        {
            return false;
        }

        var version = reader.ReadByte();
        if (version != 1)
        {
            return false;
        }

        var guidBytes = new byte[16];
        reader.ReadBytes(guidBytes);
        sessionId = new Guid(guidBytes);
        nonce = reader.ReadUInt32();
        return true;
    }

    private static uint CreateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(sizeof(uint));
        return BitConverter.ToUInt32(bytes);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BleDiscoveryService));
        }
    }
}
