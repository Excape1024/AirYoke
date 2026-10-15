package com.airyoke.link

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 协议编解码测试。
 *
 * 这是手机与 PC 之间唯一的契约。任何一侧改了字节布局而另一侧没跟上，
 * 表现会是"飞机莫名其妙乱动"——比崩溃更难查。所以这里把布局钉死。
 */
class ProtocolTest {

    private fun sampleState() = Protocol.State(
        seq = 123456,
        flags = Protocol.FLAG_CALIBRATED,
        elevator = -16000,
        aileron = 8000,
        rudder = 0,
        throttle = 32767,
        buttons = 0b1010,
        aux = 0,
        timestampMs = 987654,
    )

    // ---------- 定长校验 ----------

    @Test
    fun `状态包长度与常量一致`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        val written = Protocol.encodeState(sampleState(), buf)
        assertEquals(Protocol.STATE_SIZE, written)
    }

    @Test
    fun `遥测包长度与常量一致`() {
        val buf = ByteArray(Protocol.TELEMETRY_SIZE)
        val written = Protocol.encodeTelemetry(sampleTelemetry(), buf)
        assertEquals(Protocol.TELEMETRY_SIZE, written)
    }

    // ---------- 往返 ----------

    @Test
    fun `状态包编码后解码应还原所有字段`() {
        val original = sampleState()
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(original, buf)

        val decoded = Protocol.decodeState(buf, Protocol.STATE_SIZE)

        assertNotNull(decoded)
        assertEquals(original, decoded)
    }

    @Test
    fun `油门接管标志不会被误当成已校准`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState().copy(flags = Protocol.FLAG_THROTTLE_ENGAGED), buf)

        val decoded = Protocol.decodeState(buf, Protocol.STATE_SIZE)!!

        assertEquals(true, decoded.throttleEngaged)
        assertEquals("只置了油门位时不该被读成已校准", false, decoded.calibrated)
    }

    @Test
    fun `遥测包编码后解码应还原所有字段`() {
        val original = sampleTelemetry()
        val buf = ByteArray(Protocol.TELEMETRY_SIZE)
        Protocol.encodeTelemetry(original, buf)

        val decoded = Protocol.decodeTelemetry(buf, Protocol.TELEMETRY_SIZE)

        assertNotNull(decoded)
        assertEquals(original.seq, decoded!!.seq)
        assertEquals(original.airspeedKt, decoded.airspeedKt, 1e-4f)
        assertEquals(original.altitudeFt, decoded.altitudeFt, 1e-4f)
        assertEquals(original.headingDeg, decoded.headingDeg, 1e-4f)
        assertEquals(original.pitchDeg, decoded.pitchDeg, 1e-4f)
        assertEquals(original.bankDeg, decoded.bankDeg, 1e-4f)
        assertEquals(original.verticalSpeedFpm, decoded.verticalSpeedFpm, 1e-4f)
        assertEquals(original.gearDown, decoded.gearDown)
        assertEquals(original.flapsIndex, decoded.flapsIndex)
        assertEquals(original.autopilotOn, decoded.autopilotOn)
        assertEquals(original.linkQuality, decoded.linkQuality)
        assertEquals(original.throttlePos, decoded.throttlePos, 1e-4f)
        assertEquals(original.spoilersPos, decoded.spoilersPos, 1e-4f)
        assertEquals(original.autothrottleOn, decoded.autothrottleOn)
    }

    @Test
    fun `自动油门标志必须正确穿过链路`() {
        // 这一位决定手机能不能自学习 CL 档位。位运算或偏移写错的话，
        // 表现是"关闭接管后自动油门接不回来"，而不是报错 —— 必须有测试守着。
        val buf = ByteArray(Protocol.TELEMETRY_SIZE)

        Protocol.encodeTelemetry(sampleTelemetry().copy(autothrottleOn = false), buf)
        assertEquals(false, Protocol.decodeTelemetry(buf, Protocol.TELEMETRY_SIZE)!!.autothrottleOn)

        Protocol.encodeTelemetry(sampleTelemetry().copy(autothrottleOn = true), buf)
        assertEquals(true, Protocol.decodeTelemetry(buf, Protocol.TELEMETRY_SIZE)!!.autothrottleOn)
    }

    @Test
    fun `油门接管标志必须正确穿过链路`() {
        // 这一位决定桥接程序要不要碰推力杆，也就是自动油门警报的修复。
        // 位运算写错的话表现是"警报依旧响"，而不是报错 —— 必须有测试守着。
        val buf = ByteArray(Protocol.STATE_SIZE)

        Protocol.encodeState(sampleState().copy(flags = Protocol.FLAG_CALIBRATED), buf)
        val engagedOff = Protocol.decodeState(buf, Protocol.STATE_SIZE)!!
        assertEquals(true, engagedOff.calibrated)
        assertEquals(false, engagedOff.throttleEngaged)

        Protocol.encodeState(
            sampleState().copy(flags = Protocol.FLAG_CALIBRATED or Protocol.FLAG_THROTTLE_ENGAGED), buf,
        )
        val engagedOn = Protocol.decodeState(buf, Protocol.STATE_SIZE)!!
        assertEquals(true, engagedOn.calibrated)
        assertEquals(true, engagedOn.throttleEngaged)
    }

    @Test
    fun `负值轴量的符号必须保住`() {
        val state = sampleState().copy(elevator = -1, aileron = -32767)
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(state, buf)

        val decoded = Protocol.decodeState(buf, Protocol.STATE_SIZE)!!

        assertEquals(-1, decoded.elevator)
        assertEquals("−32767 不能被读成 +32767", -32767, decoded.aileron)
    }

    @Test
    fun `编码时把越界的轴量限幅`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState().copy(aileron = 99999), buf)

        val decoded = Protocol.decodeState(buf, Protocol.STATE_SIZE)!!

        assertEquals(Protocol.AXIS_MAX.toInt(), decoded.aileron)
    }

    // ---------- 拒收坏包 ----------

    @Test
    fun `校验和不对时拒收`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState(), buf)
        buf[10] = (buf[10] + 1).toByte() // 篡改载荷

        assertNull("被篡改的包必须拒收", Protocol.decodeState(buf, Protocol.STATE_SIZE))
    }

    @Test
    fun `魔数不对时拒收`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState(), buf)
        buf[0] = 0x00.toByte()

        assertNull(Protocol.decodeState(buf, Protocol.STATE_SIZE))
    }

    @Test
    fun `版本不一致时拒收`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState(), buf)
        buf[2] = 99

        assertNull("版本不符应明确拒收，而不是产生莫名其妙的行为", Protocol.decodeState(buf, Protocol.STATE_SIZE))
    }

    @Test
    fun `长度不足时拒收而不是越界读`() {
        val buf = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState(), buf)

        assertNull(Protocol.decodeState(buf, Protocol.STATE_SIZE - 1))
        assertNull(Protocol.decodeState(buf, 0))
    }

    @Test
    fun `全零缓冲区不会被误认成有效包`() {
        assertNull(Protocol.decodeState(ByteArray(Protocol.STATE_SIZE), Protocol.STATE_SIZE))
    }

    @Test
    fun `带偏移量的编解码 用于从大缓冲区里切包`() {
        val offset = 7
        val buf = ByteArray(Protocol.STATE_SIZE + 20)
        Protocol.encodeState(sampleState(), buf, offset)

        val decoded = Protocol.decodeState(buf, Protocol.STATE_SIZE + 20, offset)

        assertNotNull("带偏移的包应能正确解出", decoded)
        assertEquals(sampleState(), decoded)
    }

    // ---------- 发现/握手包 ----------

    @Test
    fun `握手包长度与常量一致`() {
        val buf = ByteArray(Protocol.HANDSHAKE_SIZE)
        val written = Protocol.encodeHandshake(Protocol.KIND_DISCOVER, buf)
        assertEquals(Protocol.HANDSHAKE_SIZE, written)
    }

    @Test
    fun `握手包往返保真`() {
        val buf = ByteArray(Protocol.HANDSHAKE_SIZE)

        for (kind in listOf(Protocol.KIND_DISCOVER, Protocol.KIND_BEACON)) {
            Protocol.encodeHandshake(kind, buf)
            assertEquals(kind, Protocol.decodeHandshake(buf, Protocol.HANDSHAKE_SIZE))
        }
    }

    @Test
    fun `握手包被篡改时拒收`() {
        val buf = ByteArray(Protocol.HANDSHAKE_SIZE)
        Protocol.encodeHandshake(Protocol.KIND_BEACON, buf)
        buf[3] = Protocol.KIND_DISCOVER.toByte()

        assertEquals(
            "改 kind 而不改 CRC 必须被拒 —— 否则手机可能把自己的广播当成 PC 的 beacon",
            0,
            Protocol.decodeHandshake(buf, Protocol.HANDSHAKE_SIZE),
        )
    }

    @Test
    fun `握手包与状态包不会互相误判`() {
        // 两种包都以 MAGIC 开头，长度也够，必须靠长度和 CRC 区分开
        val state = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(sampleState(), state)
        assertEquals(
            "状态包不该被认成握手包",
            0,
            Protocol.decodeHandshake(state, Protocol.STATE_SIZE),
        )

        val handshake = ByteArray(Protocol.HANDSHAKE_SIZE)
        Protocol.encodeHandshake(Protocol.KIND_BEACON, handshake)
        assertNull(
            "握手包不该被认成状态包",
            Protocol.decodeState(handshake, Protocol.HANDSHAKE_SIZE),
        )
    }

    // ---------- CRC ----------

    @Test
    fun `CRC16 与 CCITT-FALSE 标准测试向量一致`() {
        // 标准向量："123456789" → 0x29B1。
        // 拿它对照是为了确认实现没有把多项式或初值写错——
        // 否则手机和 PC 两边各自"自洽"却互不兼容，非常难查。
        val data = "123456789".toByteArray(Charsets.US_ASCII)
        assertEquals(0x29B1, Protocol.crc16(data, 0, data.size))
    }

    @Test
    fun `CRC 对单比特翻转敏感`() {
        val data = ByteArray(16) { it.toByte() }
        val base = Protocol.crc16(data, 0, data.size)

        for (i in data.indices) {
            for (bit in 0 until 8) {
                val copy = data.copyOf()
                copy[i] = (copy[i].toInt() xor (1 shl bit)).toByte()
                org.junit.Assert.assertNotEquals(
                    "第 $i 字节第 $bit 位翻转未被检出",
                    base,
                    Protocol.crc16(copy, 0, copy.size),
                )
            }
        }
    }

    // ---------- 归一化转换 ----------

    @Test
    fun `轴量归一化往返`() {
        for (v in listOf(-1f, -0.5f, 0f, 0.25f, 1f)) {
            assertEquals(v, Protocol.axisFromWire(Protocol.axisToWire(v)), 1e-4f)
        }
    }

    @Test
    fun `轴量归一化会限幅`() {
        assertEquals(1f, Protocol.axisFromWire(Protocol.axisToWire(5f)), 1e-4f)
        assertEquals(-1f, Protocol.axisFromWire(Protocol.axisToWire(-5f)), 1e-4f)
    }

    private fun sampleTelemetry() = Protocol.Telemetry(
        seq = 42,
        airspeedKt = 250.5f,
        altitudeFt = 35000f,
        headingDeg = 271.3f,
        pitchDeg = -2.5f,
        bankDeg = 15f,
        verticalSpeedFpm = -1200f,
        gearDown = false,
        flapsIndex = 2,
        autopilotOn = true,
        linkQuality = 98,
        throttlePos = 0.76f,
        spoilersPos = 0.33f,
        autothrottleOn = true,
    )
}
