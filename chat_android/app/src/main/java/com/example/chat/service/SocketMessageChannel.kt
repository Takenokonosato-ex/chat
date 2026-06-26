package com.example.chat.service

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

class SocketMessageChannel(private val socket: Socket) {
    private val inputStream = DataInputStream(socket.getInputStream())
    private val outputStream = DataOutputStream(socket.getOutputStream())

    suspend fun handshake(localSessionId: UUID): UUID = withContext(Dispatchers.IO) {
        // Send our Session ID
        val localBytes = ByteBuffer.allocate(16)
            .order(ByteOrder.LITTLE_ENDIAN)
            .putLong(localSessionId.mostSignificantBits)
            .putLong(localSessionId.leastSignificantBits)
            .array()
        outputStream.write(localBytes)
        outputStream.flush()

        // Receive remote Session ID
        val remoteBytes = ByteArray(16)
        inputStream.readFully(remoteBytes)
        val buffer = ByteBuffer.wrap(remoteBytes).order(ByteOrder.LITTLE_ENDIAN)
        val mostSig = buffer.long
        val leastSig = buffer.long
        UUID(mostSig, leastSig)
    }

    suspend fun send(message: String) = withContext(Dispatchers.IO) {
        val bytes = message.toByteArray(Charsets.UTF_8)
        val lengthBytes = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(bytes.size).array()
        outputStream.write(lengthBytes)
        outputStream.write(bytes)
        outputStream.flush()
    }

    suspend fun receive(): String? = withContext(Dispatchers.IO) {
        try {
            val lengthBytes = ByteArray(4)
            inputStream.readFully(lengthBytes)
            val length = ByteBuffer.wrap(lengthBytes).order(ByteOrder.LITTLE_ENDIAN).int
            if (length < 0 || length > 10 * 1024 * 1024) { // arbitrary 10MB limit to prevent OOM
                return@withContext null
            }
            val stringBytes = ByteArray(length)
            inputStream.readFully(stringBytes)
            String(stringBytes, Charsets.UTF_8)
        } catch (e: Exception) {
            null
        }
    }

    fun close() {
        try {
            socket.close()
        } catch (e: Exception) {
            // Ignore
        }
    }
}
