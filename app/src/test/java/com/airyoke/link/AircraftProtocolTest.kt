package com.airyoke.link

import org.junit.Assert.*
import org.junit.Test

class AircraftProtocolTest {
    private fun sample(seq: Int = 42) = Protocol.Telemetry(seq, 0f, 0f, 0f, 0f, 0f, 0f,
        false, 0, false, 100, 25f / 45, 0f, true, 1, 2, 15, 35f / 45, 83f, 84f, 12)

    @Test fun `新协议完整往返且字节位置与桥接一致`() {
        val t = sample()
        val bytes = ByteArray(Protocol.TELEMETRY_SIZE)
        assertEquals(63, Protocol.encodeTelemetry(t, bytes))
        assertEquals(5, bytes[2].toInt())
        assertEquals(2, bytes[45].toInt())
        assertEquals(15, bytes[46].toInt())
        val view = java.nio.ByteBuffer.wrap(bytes).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        assertEquals(t.throttlePos2, view.getFloat(47), 0f)
        assertEquals(83f, view.getFloat(51), 0f)
        assertEquals(84f, view.getFloat(55), 0f)
        assertEquals(12, view.getShort(59).toInt())
        assertEquals(t, Protocol.decodeTelemetry(bytes, bytes.size))
        bytes[2] = 4
        assertNull(Protocol.decodeTelemetry(bytes, bytes.size))
    }

    @Test fun `每一个拆包位置和逐字节输入均不能丢帧`() {
        val bytes = ByteArray(Protocol.TELEMETRY_SIZE)
        Protocol.encodeTelemetry(sample(), bytes)
        for (cut in 1 until bytes.size) {
            val decoder = TelemetryStreamDecoder()
            val result = mutableListOf<Protocol.Telemetry>()
            decoder.feed(bytes.copyOfRange(0, cut), cut, result::add)
            assertTrue(result.isEmpty())
            decoder.feed(bytes.copyOfRange(cut, bytes.size), bytes.size - cut, result::add)
            assertEquals(listOf(sample()), result)
        }
        val decoder = TelemetryStreamDecoder()
        val result = mutableListOf<Protocol.Telemetry>()
        bytes.forEach { decoder.feed(byteArrayOf(it), 1, result::add) }
        assertEquals(listOf(sample()), result)
    }

    @Test fun `粘包和坏包后应恢复同步`() {
        val first = ByteArray(63); val second = ByteArray(63)
        Protocol.encodeTelemetry(sample(), first); Protocol.encodeTelemetry(sample(43), second)
        val bad = first.clone().also { it[30] = 123 }
        val all = byteArrayOf(1, 2, 3) + bad + first + second
        val result = mutableListOf<Protocol.Telemetry>()
        TelemetryStreamDecoder().feed(all, all.size, result::add)
        assertEquals(listOf(sample(), sample(43)), result)
    }

    @Test fun `机模位不能污染校准和接管标志`() {
        for (aircraft in 1..2) {
            val state = Protocol.State(1, (aircraft shl 3) or 5, 0, 0, 0, 0, 0, 0, 0)
            val bytes = ByteArray(26); Protocol.encodeState(state, bytes)
            val back = Protocol.decodeState(bytes, bytes.size)!!
            assertEquals(aircraft, back.aircraft); assertTrue(back.calibrated); assertTrue(back.throttleEngaged)
        }
    }
}
