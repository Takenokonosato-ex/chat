package com.example.chat.service

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.util.Log
import com.example.chat.model.BlePeer
import com.example.chat.model.ChatSessionPayload
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

@SuppressLint("MissingPermission")
class BleDiscoveryService(private val context: Context, val localSession: ChatSessionPayload) {
    private val bluetoothManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val bluetoothAdapter = bluetoothManager.adapter
    private val advertiser = bluetoothAdapter?.bluetoothLeAdvertiser
    private val scanner = bluetoothAdapter?.bluetoothLeScanner

    private val _publisherStatus = MutableStateFlow("Advertising: Stopped")
    val publisherStatus: StateFlow<String> = _publisherStatus.asStateFlow()

    private val _watcherStatus = MutableStateFlow("Scanning: Stopped")
    val watcherStatus: StateFlow<String> = _watcherStatus.asStateFlow()

    private val _errorOccurred = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val errorOccurred: SharedFlow<String> = _errorOccurred.asSharedFlow()

    private val _peerDiscovered = MutableSharedFlow<BlePeer>(extraBufferCapacity = 10)
    val peerDiscovered: SharedFlow<BlePeer> = _peerDiscovered.asSharedFlow()

    var isAdvertising = false
        private set
    var isScanning = false
        private set

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
            isAdvertising = true
            _publisherStatus.value = "Advertising: Started"
            Log.d("BleDiscovery", "BLE advertise started")
        }

        override fun onStartFailure(errorCode: Int) {
            isAdvertising = false
            _publisherStatus.value = "Advertising: Failed ($errorCode)"
            _errorOccurred.tryEmit("BLE advertise start failed: error code $errorCode")
            Log.e("BleDiscovery", "BLE advertise failed: $errorCode")
        }
    }

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult?) {
            result ?: return
            val record = result.scanRecord ?: return
            val manufacturerData = record.getManufacturerSpecificData(ChatSessionPayload.BLE_COMPANY_ID) ?: return

            val session = ChatSessionPayload.tryParse(manufacturerData) ?: return

            if (session.sessionId == localSession.sessionId) {
                return
            }

            val peer = BlePeer(
                macAddress = result.device.address,
                sessionId = session.sessionId,
                nonce = session.nonce,
                rssi = result.rssi,
                timestamp = System.currentTimeMillis()
            )
            _peerDiscovered.tryEmit(peer)
        }

        override fun onScanFailed(errorCode: Int) {
            isScanning = false
            _watcherStatus.value = "Scanning: Failed ($errorCode)"
            _errorOccurred.tryEmit("BLE scan failed: error code $errorCode")
            Log.e("BleDiscovery", "BLE scan failed: $errorCode")
        }
    }

    fun startAdvertising() {
        if (isAdvertising || advertiser == null) return

        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_BALANCED)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .setConnectable(false)
            .build()

        // Empty advertise data, relies on default flags
        val advertiseData = AdvertiseData.Builder()
            .build()

        // Put the 25-byte payload in the scan response to avoid exceeding the 31-byte limit
        val scanResponseData = AdvertiseData.Builder()
            .addManufacturerData(ChatSessionPayload.BLE_COMPANY_ID, localSession.toBuffer())
            .build()

        try {
            advertiser.startAdvertising(settings, advertiseData, scanResponseData, advertiseCallback)
            _publisherStatus.value = "Advertising: Starting..."
        } catch (e: Exception) {
            _errorOccurred.tryEmit("BLE advertise start exception: ${e.message}")
        }
    }

    fun stopAdvertising() {
        if (!isAdvertising || advertiser == null) return
        try {
            advertiser.stopAdvertising(advertiseCallback)
            isAdvertising = false
            _publisherStatus.value = "Advertising: Stopped"
        } catch (e: Exception) {
            _errorOccurred.tryEmit("BLE advertise stop exception: ${e.message}")
        }
    }

    fun startScanning() {
        if (isScanning || scanner == null) return

        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_BALANCED)
            .build()

        val filter = ScanFilter.Builder()
            // We just match on manufacturer data ID to be safe
            // We could also match the MAGIC payload
            .build()

        try {
            scanner.startScan(listOf(filter), settings, scanCallback)
            isScanning = true
            _watcherStatus.value = "Scanning: Started"
        } catch (e: Exception) {
            _errorOccurred.tryEmit("BLE scan start exception: ${e.message}")
        }
    }

    fun stopScanning() {
        if (!isScanning || scanner == null) return
        try {
            scanner.stopScan(scanCallback)
            isScanning = false
            _watcherStatus.value = "Scanning: Stopped"
        } catch (e: Exception) {
            _errorOccurred.tryEmit("BLE scan stop exception: ${e.message}")
        }
    }

    fun dispose() {
        stopAdvertising()
        stopScanning()
    }
}
