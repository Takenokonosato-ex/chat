package com.example.chat.model

import java.util.UUID

data class BlePeer(
    val macAddress: String,
    val sessionId: UUID,
    val nonce: Int,
    val rssi: Int,
    val timestamp: Long
) {
    val pcName: String
        get() = ChatSessionPayload.decodeNameFromGuid(sessionId)
}
