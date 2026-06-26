package com.example.chat.service

import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pDevice
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.net.wifi.p2p.WifiP2pManager.Channel
import android.util.Log
import com.example.chat.model.BlePeer
import com.example.chat.model.ChatSessionPayload
import com.example.chat.model.WifiDirectPeer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap

@SuppressLint("MissingPermission")
class WifiDirectChatService(
    private val context: Context,
    private val localSession: ChatSessionPayload
) {
    private val wifiP2pManager = context.getSystemService(Context.WIFI_P2P_SERVICE) as WifiP2pManager
    private var channel: Channel? = null
    private var receiver: BroadcastReceiver? = null

    private val _status = MutableStateFlow("Wi-Fi Direct: Stopped")
    val status: StateFlow<String> = _status.asStateFlow()

    private val _errorOccurred = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val errorOccurred: SharedFlow<String> = _errorOccurred.asSharedFlow()

    private val _peerDiscovered = MutableSharedFlow<WifiDirectPeer>(extraBufferCapacity = 10)
    val peerDiscovered: SharedFlow<WifiDirectPeer> = _peerDiscovered.asSharedFlow()

    private val _messageReceived = MutableSharedFlow<String>(extraBufferCapacity = 100)
    val messageReceived: SharedFlow<String> = _messageReceived.asSharedFlow()

    private val _connectionState = MutableStateFlow(false)
    val connectionState: StateFlow<Boolean> = _connectionState.asStateFlow()

    private val blePeers = ConcurrentHashMap<ChatSessionPayload, BlePeer>()
    private val pendingDevices = mutableListOf<WifiP2pDevice>()
    private val wifiPeers = ConcurrentHashMap<String, WifiDirectPeer>() // deviceAddress to Peer

    var isStarted = false
        private set
    var isConnected = false
        private set
    private var isConnecting = false

    private var socketChannel: SocketMessageChannel? = null
    private var serverSocket: ServerSocket? = null
    private var receiveJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO)

    fun start() {
        if (isStarted) return
        channel = wifiP2pManager.initialize(context, context.mainLooper, null)

        val intentFilter = IntentFilter().apply {
            addAction(WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_THIS_DEVICE_CHANGED_ACTION)
        }

        receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                when (intent.action) {
                    WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION -> {
                        wifiP2pManager.requestPeers(channel) { peers ->
                            handlePeersChanged(peers.deviceList)
                        }
                    }
                    WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION -> {
                        val networkInfo = intent.getParcelableExtra<android.net.NetworkInfo>(WifiP2pManager.EXTRA_NETWORK_INFO)
                        if (networkInfo?.isConnected == true) {
                            wifiP2pManager.requestConnectionInfo(channel) { info ->
                                handleConnectionInfo(info)
                            }
                        } else {
                            closeConnection()
                        }
                    }
                }
            }
        }

        context.registerReceiver(receiver, intentFilter)

        wifiP2pManager.discoverPeers(channel, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                isStarted = true
                _status.value = "Wi-Fi Direct: Discovering peers..."
            }
            override fun onFailure(reason: Int) {
                _errorOccurred.tryEmit("Wi-Fi Direct discover peers failed: $reason")
            }
        })
    }

    fun stop() {
        if (!isStarted) return
        closeConnection()

        receiver?.let {
            try {
                context.unregisterReceiver(it)
            } catch (e: Exception) {}
            receiver = null
        }

        wifiP2pManager.stopPeerDiscovery(channel, null)
        isStarted = false
        _status.value = "Wi-Fi Direct: Stopped"
    }

    fun addBlePeer(peer: BlePeer) {
        val session = ChatSessionPayload(peer.sessionId, peer.nonce)
        blePeers[session] = peer

        // Check if we already saw this device via Wi-Fi P2P
        val matchedDevice = pendingDevices.firstOrNull { it.deviceName?.contains(peer.pcName, ignoreCase = true) == true }
        if (matchedDevice != null) {
            pendingDevices.remove(matchedDevice)
            val wifiPeer = WifiDirectPeer(matchedDevice, session)
            wifiPeers[matchedDevice.deviceAddress] = wifiPeer
            _peerDiscovered.tryEmit(wifiPeer)

            if (shouldInitiateConnection(session)) {
                connectToPeer(wifiPeer)
            }
        }
    }

    private fun handlePeersChanged(devices: Collection<WifiP2pDevice>) {
        for (device in devices) {
            val name = device.deviceName ?: continue
            val matchedBleSession = blePeers.entries.firstOrNull {
                val bleName = it.value.pcName
                name.contains(bleName, ignoreCase = true)
            }?.key

            if (matchedBleSession != null) {
                if (!wifiPeers.containsKey(device.deviceAddress)) {
                    val peer = WifiDirectPeer(device, matchedBleSession)
                    wifiPeers[device.deviceAddress] = peer
                    _peerDiscovered.tryEmit(peer)

                    if (shouldInitiateConnection(matchedBleSession)) {
                        connectToPeer(peer)
                    }
                }
            } else {
                if (!pendingDevices.any { it.deviceAddress == device.deviceAddress }) {
                    pendingDevices.add(device)
                }
            }
        }
    }

    private fun shouldInitiateConnection(remoteSession: ChatSessionPayload): Boolean {
        val cmp = localSession.sessionId.compareTo(remoteSession.sessionId)
        if (cmp != 0) return cmp < 0
        return localSession.nonce < remoteSession.nonce
    }

    fun connectToPeer(peer: WifiDirectPeer) {
        if (isConnected || isConnecting) return
        isConnecting = true
        _status.value = "Wi-Fi Direct: Connecting to ${peer.deviceName}..."

        val config = WifiP2pConfig().apply {
            deviceAddress = peer.deviceAddress
            // Use Push Button Configuration
            wps.setup = android.net.wifi.WpsInfo.PBC
        }

        wifiP2pManager.connect(channel, config, object : WifiP2pManager.ActionListener {
            override fun onSuccess() {
                // Wait for ConnectionInfo
                Log.d("WifiDirect", "P2P Connect initiated")
            }
            override fun onFailure(reason: Int) {
                isConnecting = false
                _errorOccurred.tryEmit("P2P Connect failed: $reason")
            }
        })
    }

    private fun handleConnectionInfo(info: WifiP2pInfo) {
        if (!info.groupFormed) return

        isConnecting = false
        isConnected = true
        _status.value = "Wi-Fi Direct: Group Formed. isGO=${info.isGroupOwner}"

        scope.launch {
            try {
                val socket = if (info.isGroupOwner) {
                    serverSocket = ServerSocket(50001).apply { reuseAddress = true }
                    _status.value = "Wi-Fi Direct: Listening on 50001"
                    serverSocket!!.accept()
                } else {
                    _status.value = "Wi-Fi Direct: Connecting to ${info.groupOwnerAddress.hostAddress}:50001"
                    val s = Socket()
                    // Allow some time for GO to start listening
                    delay(2000)
                    s.connect(InetSocketAddress(info.groupOwnerAddress, 50001), 10000)
                    s
                }

                socketChannel = SocketMessageChannel(socket)
                _status.value = "Wi-Fi Direct: Socket connected. Handshaking..."

                val remoteSessionId = socketChannel!!.handshake(localSession.sessionId)
                Log.d("WifiDirect", "Handshake success, remote=$remoteSessionId")
                
                _status.value = "Wi-Fi Direct: Connected"
                _connectionState.value = true

                receiveLoop(socketChannel!!)
            } catch (e: Exception) {
                _errorOccurred.tryEmit("Socket error: ${e.message}")
                closeConnection()
            }
        }
    }

    private suspend fun receiveLoop(channel: SocketMessageChannel) {
        while (isConnected && socketChannel == channel) {
            val msg = channel.receive()
            if (msg == null) {
                closeConnection()
                break
            }
            _messageReceived.tryEmit(msg)
        }
    }

    fun sendMessage(msg: String) {
        val channel = socketChannel
        if (channel == null) {
            _errorOccurred.tryEmit("Not connected")
            return
        }
        scope.launch {
            try {
                channel.send(msg)
            } catch (e: Exception) {
                _errorOccurred.tryEmit("Send failed: ${e.message}")
                closeConnection()
            }
        }
    }

    fun closeConnection() {
        socketChannel?.close()
        socketChannel = null
        try { serverSocket?.close() } catch (e: Exception) {}
        serverSocket = null

        if (isConnected) {
            wifiP2pManager.removeGroup(channel, null)
        }

        isConnected = false
        isConnecting = false
        _connectionState.value = false
        _status.value = "Wi-Fi Direct: Disconnected"
    }

    fun dispose() {
        stop()
    }
}
