package com.example.chat.model

import android.net.wifi.p2p.WifiP2pDevice

data class WifiDirectPeer(
    val device: WifiP2pDevice,
    val session: ChatSessionPayload
) {
    val deviceName: String
        get() = device.deviceName

    val deviceAddress: String
        get() = device.deviceAddress
}
