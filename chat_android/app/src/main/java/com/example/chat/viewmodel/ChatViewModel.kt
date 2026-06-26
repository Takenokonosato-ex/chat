package com.example.chat.viewmodel

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.example.chat.model.BlePeer
import com.example.chat.model.ChatSessionPayload
import com.example.chat.model.WifiDirectPeer
import com.example.chat.service.BleDiscoveryService
import com.example.chat.service.WifiDirectChatService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

data class ChatMessage(
    val message: String,
    val senderName: String,
    val isMe: Boolean,
    val timestamp: String
)

class ChatViewModel(application: Application) : AndroidViewModel(application) {
    private val deviceName = try {
        android.provider.Settings.Global.getString(application.contentResolver, android.provider.Settings.Global.DEVICE_NAME) ?: Build.MODEL
    } catch (e: Exception) {
        Build.MODEL
    }
    val localSession = ChatSessionPayload.createLocal(deviceName)

    private val bleService = BleDiscoveryService(application, localSession)
    private val wifiService = WifiDirectChatService(application, localSession)

    val publisherStatus = bleService.publisherStatus
    val watcherStatus = bleService.watcherStatus
    val wifiStatus = wifiService.status
    val isConnected = wifiService.connectionState

    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    private val _peers = MutableStateFlow<List<WifiDirectPeer>>(emptyList())
    val peers: StateFlow<List<WifiDirectPeer>> = _peers.asStateFlow()

    private val _blePeers = MutableStateFlow<List<BlePeer>>(emptyList())
    val blePeers: StateFlow<List<BlePeer>> = _blePeers.asStateFlow()

    private val _errors = MutableStateFlow<String?>(null)
    val errors: StateFlow<String?> = _errors.asStateFlow()

    private val _logs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = _logs.asStateFlow()

    fun logSystemMessage(msg: String) {
        val time = SimpleDateFormat("HH:mm:ss", Locale.US).format(Date())
        val formatted = "[$time] $msg"
        _logs.value = (_logs.value + formatted).takeLast(100) // Keep last 100 logs
    }

    init {
        viewModelScope.launch {
            bleService.errorOccurred.collect { 
                _errors.value = it 
                logSystemMessage("BLE Error: $it")
            }
        }
        viewModelScope.launch {
            wifiService.errorOccurred.collect { 
                _errors.value = it 
                logSystemMessage("WiFi Error: $it")
            }
        }
        viewModelScope.launch {
            bleService.peerDiscovered.collect { peer ->
                logSystemMessage("BLE Discovered: ${peer.pcName}")
                val currentList = _blePeers.value.toMutableList()
                currentList.removeAll { it.sessionId == peer.sessionId }
                currentList.add(peer)
                _blePeers.value = currentList

                wifiService.addBlePeer(peer)
            }
        }
        viewModelScope.launch {
            wifiService.peerDiscovered.collect { peer ->
                logSystemMessage("WiFi Discovered: ${peer.device.deviceName}")
                val currentList = _peers.value.toMutableList()
                currentList.removeAll { it.session.sessionId == peer.session.sessionId }
                currentList.add(peer)
                _peers.value = currentList
            }
        }
        viewModelScope.launch {
            wifiService.messageReceived.collect { msg ->
                if (msg.startsWith("[System]")) {
                    logSystemMessage(msg.removePrefix("[System] ").trim())
                } else {
                    addMessage(msg, "Peer", false)
                }
            }
        }
        viewModelScope.launch { publisherStatus.collect { logSystemMessage(it) } }
        viewModelScope.launch { watcherStatus.collect { logSystemMessage(it) } }
        viewModelScope.launch { wifiStatus.collect { logSystemMessage(it) } }
    }

    fun startAdvertising() = bleService.startAdvertising()
    fun stopAdvertising() = bleService.stopAdvertising()
    fun startScanning() = bleService.startScanning()
    fun stopScanning() = bleService.stopScanning()
    fun startWifi() = wifiService.start()
    fun stopWifi() = wifiService.stop()

    fun connectToPeer(peer: WifiDirectPeer) {
        wifiService.connectToPeer(peer)
    }

    fun disconnect() {
        wifiService.closeConnection()
    }

    fun sendMessage(text: String) {
        if (text.isBlank()) return
        wifiService.sendMessage(text)
        addMessage(text, deviceName, true)
    }

    fun clearError() {
        _errors.value = null
    }

    private fun addMessage(text: String, sender: String, isMe: Boolean) {
        val sdf = SimpleDateFormat("HH:mm", Locale.getDefault())
        val msg = ChatMessage(text, sender, isMe, sdf.format(Date()))
        _messages.value = _messages.value + msg
    }

    override fun onCleared() {
        super.onCleared()
        bleService.dispose()
        wifiService.dispose()
    }
}
