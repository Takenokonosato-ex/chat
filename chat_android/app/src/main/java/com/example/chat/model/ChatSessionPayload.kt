package com.example.chat.model

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import kotlin.random.Random

data class ChatSessionPayload(val sessionId: UUID, val nonce: Int) {
    companion object {
        const val VERSION: Byte = 1
        const val BLE_COMPANY_ID: Int = 0xFFFF
        val MAGIC = "CHAT".toByteArray(Charsets.US_ASCII)

        fun createLocal(deviceName: String): ChatSessionPayload {
            return ChatSessionPayload(encodeNameToGuid(deviceName), createNonce())
        }

        private fun encodeNameToGuid(name: String): UUID {
            val bytes = ByteArray(16)
            val nameBytes = name.toByteArray(Charsets.UTF_8)
            System.arraycopy(nameBytes, 0, bytes, 0, minOf(nameBytes.size, 16))
            // C# `new Guid(byte[])` uses a specific mixed-endian layout:
            // Data1 (4 bytes LE), Data2 (2 bytes LE), Data3 (2 bytes LE), Data4 (8 bytes BE).
            // But we can just store the bytes directly into the most/least significant bits.
            // As long as we read it back the same way, the bytes will be identical over the air.
            val bb = ByteBuffer.wrap(bytes)
            val mostSigBits = bb.long
            val leastSigBits = bb.long
            return UUID(mostSigBits, leastSigBits)
        }

        fun decodeNameFromGuid(guid: UUID): String {
            val bb = ByteBuffer.allocate(16)
            bb.putLong(guid.mostSignificantBits)
            bb.putLong(guid.leastSignificantBits)
            val bytes = bb.array()
            var len = bytes.indexOfFirst { it == 0.toByte() }
            if (len < 0) len = 16
            return String(bytes, 0, len, Charsets.UTF_8)
        }

        fun tryParse(payload: ByteArray): ChatSessionPayload? {
            if (payload.size < MAGIC.size + 1 + 16 + 4) {
                return null
            }

            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
            val magic = ByteArray(MAGIC.size)
            buffer.get(magic)

            if (!magic.contentEquals(MAGIC)) {
                return null
            }

            val version = buffer.get()
            if (version != VERSION) {
                return null
            }

            val guidBytes = ByteArray(16)
            buffer.get(guidBytes)
            
            val guidBb = ByteBuffer.wrap(guidBytes)
            val mostSig = guidBb.long
            val leastSig = guidBb.long
            val sessionId = UUID(mostSig, leastSig)

            val nonce = buffer.int
            return ChatSessionPayload(sessionId, nonce)
        }

        private fun createNonce(): Int {
            return Random.nextInt()
        }
    }

    fun toBuffer(): ByteArray {
        val buffer = ByteBuffer.allocate(25).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(MAGIC)
        buffer.put(VERSION)
        
        val guidBb = ByteBuffer.allocate(16)
        guidBb.putLong(sessionId.mostSignificantBits)
        guidBb.putLong(sessionId.leastSignificantBits)
        buffer.put(guidBb.array())
        
        buffer.putInt(nonce)
        return buffer.array()
    }
}
