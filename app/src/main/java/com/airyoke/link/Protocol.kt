package com.airyoke.link

/**
 * 手机 ↔ PC 的二进制协议。
 *
 * 两种有线通道（USB 网络共享的 UDP、adb reverse 的 TCP）**复用同一套定长小包**，
 * 所以协议层与传输层完全解耦 —— 换通道不影响这里。
 *
 * 全部小端序。数值用 i16 承载 −32767..32767 的归一化轴量：
 * 直接用 16 位而不是 8 位，是因为 8 位（256 级）在操纵面上会有可见的台阶感。
 *
 * 纯 Kotlin，不依赖 Android，可在 JVM 上单元测试。
 */
object Protocol {

    /** 包魔数，用来在混杂的 UDP 流量里识别自己的包。 */
    const val MAGIC = 0xA1C0

    /** 协议五：机模选择、双杆、双发 N1 及独立反馈有效性。 */
    const val VERSION = 5

    /** 状态包按键 bit6：只断开自动驾驶，绝不切换为接通。 */
    const val BUTTON_AP_DISCONNECT = 0x40
    /** 遥测状态 bit3：桥接端支持专用断开指令。 */
    const val SYSTEM_AP_DISCONNECT_SUPPORTED = 0x08

    /** 默认端口。手机与桥接程序必须一致。 */
    const val PORT = 9000

    /** 手机 → PC 的状态包长度。 */
    const val STATE_SIZE = 26

    /** PC → 手机 的遥测包长度。 */
    const val TELEMETRY_SIZE = 63
    const val ENGINE_LEVERS_VALID = 1
    const val ENGINE_N1_VALID = 2
    const val ENGINE_CONTROL_READY = 4
    const val ENGINE_REVERSE_READY = 8
    const val ENGINE_MODEL_MISMATCH = 16

    // ---- 状态包 flags 位 ----
    /** 已完成校准。未校准时桥接程序可以据此拒绝输出，避免飞机乱动。 */
    const val FLAG_CALIBRATED = 0x01
    /** 链路探测包，不携带有效操纵数据。 */
    const val FLAG_LINK_PROBE = 0x02

    /** 手机接管正推力或正在发送交还 CL 目标；与手指是否按住分开。 */
    const val FLAG_THROTTLE_ENGAGED = 0x04

    /** 轴量的满量程。 */
    const val AXIS_MAX = 32767f

    // ================= 发现/握手包 =================

    /** 发现包长度。 */
    const val HANDSHAKE_SIZE = 6

    /** 手机 → PC：请求桥接程序表明身份。 */
    const val KIND_DISCOVER = 1

    /** PC → 手机：桥接程序在这里，以及它的链路状态。 */
    const val KIND_BEACON = 2

    /**
     * 手机与 PC 互相认领对方用的最小握手包。
     *
     * UDP 通道下，手机把 [KIND_DISCOVER] 发到广播地址，桥接程序回 [KIND_BEACON]；
     * 手机从回包的**源地址**得知桥接程序在哪，不需要预先配置任何 IP。
     * 桥接程序也会主动周期性广播 [KIND_BEACON]，两条路都通，发现更快。
     */
    fun encodeHandshake(kind: Int, out: ByteArray, offset: Int = 0): Int {
        var i = offset
        i = putU16(out, i, MAGIC)
        out[i++] = VERSION.toByte()
        out[i++] = kind.toByte()
        val crc = crc16(out, offset, i - offset)
        i = putU16(out, i, crc)
        return i - offset
    }

    /** 返回 kind，非法包返回 0。 */
    fun decodeHandshake(buf: ByteArray, length: Int, offset: Int = 0): Int {
        if (length - offset < HANDSHAKE_SIZE) return 0
        if (readU16(buf, offset) != MAGIC) return 0
        if (buf[offset + 2].toInt() and 0xFF != VERSION) return 0
        val expected = readU16(buf, offset + HANDSHAKE_SIZE - 2)
        if (expected != crc16(buf, offset, HANDSHAKE_SIZE - 2)) return 0
        return buf[offset + 3].toInt() and 0xFF
    }

    // ================= 状态包（手机 → PC） =================

    /**
     * 一帧操纵状态。
     *
     * [elevator] / [aileron] / [rudder] / [throttle] 的语义见
     * [com.airyoke.core.ControlOutput] —— 正方向分别是抬头、右滚、右偏航、满推力。
     */
    data class State(
        val seq: Int,
        val flags: Int,
        val elevator: Int,
        val aileron: Int,
        val rudder: Int,
        val throttle: Int,
        val buttons: Int,
        val aux: Int,
        val timestampMs: Int,
    ) {
        /** 已完成校准。未校准时桥接程序拒绝输出，避免飞机乱动。 */
        val calibrated: Boolean get() = (flags and FLAG_CALIBRATED) != 0

        /** 手机是否正在写入推力目标。见 [FLAG_THROTTLE_ENGAGED]。 */
        val throttleEngaged: Boolean get() = (flags and FLAG_THROTTLE_ENGAGED) != 0
        val aircraft: Int get() = (flags ushr 3) and 3
    }

    /**
     * 把一帧状态编码进 [out]（长度至少 [STATE_SIZE]），返回实际写入的字节数。
     *
     * 复用外部传入的数组是为了 100Hz 下不产生垃圾 —— 这个函数每秒被调用 100 次。
     */
    fun encodeState(state: State, out: ByteArray, offset: Int = 0): Int {
        var i = offset
        i = putU16(out, i, MAGIC)
        out[i++] = VERSION.toByte()
        out[i++] = state.flags.toByte()
        i = putI32(out, i, state.seq)
        i = putI16(out, i, state.elevator)
        i = putI16(out, i, state.aileron)
        i = putI16(out, i, state.rudder)
        i = putI16(out, i, state.throttle)
        i = putU16(out, i, state.buttons)
        i = putU16(out, i, state.aux)
        i = putI32(out, i, state.timestampMs)
        val crc = crc16(out, offset, i - offset)
        i = putU16(out, i, crc)
        return i - offset
    }

    /**
     * 解码状态包。任何校验不过都返回 null —— 宁可不动作，也不要把垃圾数据当成舵量送进飞机。
     */
    fun decodeState(buf: ByteArray, length: Int, offset: Int = 0): State? {
        if (length - offset < STATE_SIZE) return null
        var i = offset
        if (readU16(buf, i) != MAGIC) return null
        i += 2
        if (buf[i++].toInt() and 0xFF != VERSION) return null

        val expectedCrc = readU16(buf, offset + STATE_SIZE - 2)
        val actualCrc = crc16(buf, offset, STATE_SIZE - 2)
        if (expectedCrc != actualCrc) return null

        val flags = buf[i++].toInt() and 0xFF
        val seq = readI32(buf, i); i += 4
        val elevator = readI16(buf, i); i += 2
        val aileron = readI16(buf, i); i += 2
        val rudder = readI16(buf, i); i += 2
        val throttle = readI16(buf, i); i += 2
        val buttons = readU16(buf, i); i += 2
        val aux = readU16(buf, i); i += 2
        val timestampMs = readI32(buf, i)

        return State(seq, flags, elevator, aileron, rudder, throttle, buttons, aux, timestampMs)
    }

    // ================= 遥测包（PC → 手机） =================

    /** 桥接程序回传的飞行数据，显示在手机界面上。 */
    data class Telemetry(
        val seq: Int,
        val airspeedKt: Float,
        val altitudeFt: Float,
        val headingDeg: Float,
        val pitchDeg: Float,
        val bankDeg: Float,
        val verticalSpeedFpm: Float,
        val gearDown: Boolean,
        val flapsIndex: Int,
        val autopilotOn: Boolean,
        /** 链路质量 0..100，用于界面显示丢包情况。 */
        val linkQuality: Int,
        /** 一号座舱杆位。空客适配为杆角归一化，与发动机 N1 独立。 */
        val throttlePos: Float,
        /** 减速板手柄位置 0..1。 */
        val spoilersPos: Float,
        /** 自动油门接通状态，仅用于显示，不用于推断 CL 档位。 */
        val autothrottleOn: Boolean,
        val systemFlags: Int = 0,
        val aircraft: Int = 0,
        val engineFlags: Int = 0,
        val throttlePos2: Float = 0f,
        val n1Left: Float = 0f,
        val n1Right: Float = 0f,
        val engineAgeMs: Int = 65535,
    )

    fun encodeTelemetry(t: Telemetry, out: ByteArray, offset: Int = 0): Int {
        var i = offset
        i = putU16(out, i, MAGIC)
        out[i++] = VERSION.toByte()
        out[i++] = t.systemFlags.toByte()
        i = putI32(out, i, t.seq)
        i = putF32(out, i, t.airspeedKt)
        i = putF32(out, i, t.altitudeFt)
        i = putF32(out, i, t.headingDeg)
        i = putF32(out, i, t.pitchDeg)
        i = putF32(out, i, t.bankDeg)
        i = putF32(out, i, t.verticalSpeedFpm)
        out[i++] = if (t.gearDown) 1 else 0
        out[i++] = t.flapsIndex.toByte()
        out[i++] = if (t.autopilotOn) 1 else 0
        out[i++] = t.linkQuality.toByte()
        i = putF32(out, i, t.throttlePos)
        i = putF32(out, i, t.spoilersPos)
        out[i++] = if (t.autothrottleOn) 1 else 0
        out[i++] = t.aircraft.toByte()
        out[i++] = t.engineFlags.toByte()
        i = putF32(out, i, t.throttlePos2)
        i = putF32(out, i, t.n1Left)
        i = putF32(out, i, t.n1Right)
        i = putU16(out, i, t.engineAgeMs)
        val crc = crc16(out, offset, i - offset)
        i = putU16(out, i, crc)
        return i - offset
    }

    fun decodeTelemetry(buf: ByteArray, length: Int, offset: Int = 0): Telemetry? {
        if (length - offset < TELEMETRY_SIZE) return null
        if (readU16(buf, offset) != MAGIC) return null
        if (buf[offset + 2].toInt() and 0xFF != VERSION) return null

        val expectedCrc = readU16(buf, offset + TELEMETRY_SIZE - 2)
        if (expectedCrc != crc16(buf, offset, TELEMETRY_SIZE - 2)) return null

        var i = offset + 4
        val seq = readI32(buf, i); i += 4
        val airspeed = readF32(buf, i); i += 4
        val altitude = readF32(buf, i); i += 4
        val heading = readF32(buf, i); i += 4
        val pitch = readF32(buf, i); i += 4
        val bank = readF32(buf, i); i += 4
        val verticalSpeed = readF32(buf, i); i += 4
        val gearDown = buf[i++].toInt() != 0
        val flaps = buf[i++].toInt() and 0xFF
        val ap = buf[i++].toInt() != 0
        val quality = buf[i].toInt() and 0xFF
        i += 1
        val throttlePos = readF32(buf, i); i += 4
        val spoilersPos = readF32(buf, i); i += 4
        val autothrottleOn = buf[i++].toInt() != 0
        val aircraft = buf[i++].toInt() and 0xFF
        val engineFlags = buf[i++].toInt() and 0xFF
        val throttlePos2 = readF32(buf, i); i += 4
        val n1Left = readF32(buf, i); i += 4
        val n1Right = readF32(buf, i); i += 4
        val engineAgeMs = readU16(buf, i)

        return Telemetry(
            seq, airspeed, altitude, heading, pitch, bank, verticalSpeed,
            gearDown, flaps, ap, quality, throttlePos, spoilersPos, autothrottleOn,
            buf[offset + 3].toInt() and 0xFF,
            aircraft, engineFlags, throttlePos2, n1Left, n1Right, engineAgeMs,
        )
    }

    // ================= 校验 =================

    /**
     * CRC-16/CCITT-FALSE，多项式 0x1021，初值 0xFFFF。
     *
     * 选它是因为计算便宜（查表或移位都行），而链路本身是 USB 有线、误码率极低，
     * 这里主要防的是"把别的程序的 UDP 包误认成我们的"，而不是纠错。
     */
    fun crc16(buf: ByteArray, offset: Int, length: Int): Int {
        var crc = 0xFFFF
        for (k in offset until offset + length) {
            crc = crc xor ((buf[k].toInt() and 0xFF) shl 8)
            for (bit in 0 until 8) {
                crc = if (crc and 0x8000 != 0) {
                    ((crc shl 1) xor 0x1021) and 0xFFFF
                } else {
                    (crc shl 1) and 0xFFFF
                }
            }
        }
        return crc
    }

    // ================= 字节读写（小端） =================

    private fun putU16(buf: ByteArray, i: Int, v: Int): Int {
        buf[i] = (v and 0xFF).toByte()
        buf[i + 1] = ((v ushr 8) and 0xFF).toByte()
        return i + 2
    }

    private fun putI16(buf: ByteArray, i: Int, v: Int): Int {
        val c = v.coerceIn(-AXIS_MAX.toInt(), AXIS_MAX.toInt())
        buf[i] = (c and 0xFF).toByte()
        buf[i + 1] = ((c ushr 8) and 0xFF).toByte()
        return i + 2
    }

    private fun putI32(buf: ByteArray, i: Int, v: Int): Int {
        buf[i] = (v and 0xFF).toByte()
        buf[i + 1] = ((v ushr 8) and 0xFF).toByte()
        buf[i + 2] = ((v ushr 16) and 0xFF).toByte()
        buf[i + 3] = ((v ushr 24) and 0xFF).toByte()
        return i + 4
    }

    private fun putF32(buf: ByteArray, i: Int, v: Float): Int =
        putI32(buf, i, java.lang.Float.floatToIntBits(v))

    private fun readU16(buf: ByteArray, i: Int): Int =
        (buf[i].toInt() and 0xFF) or ((buf[i + 1].toInt() and 0xFF) shl 8)

    private fun readI16(buf: ByteArray, i: Int): Int =
        readU16(buf, i).toShort().toInt()

    private fun readI32(buf: ByteArray, i: Int): Int =
        (buf[i].toInt() and 0xFF) or
            ((buf[i + 1].toInt() and 0xFF) shl 8) or
            ((buf[i + 2].toInt() and 0xFF) shl 16) or
            ((buf[i + 3].toInt() and 0xFF) shl 24)

    private fun readF32(buf: ByteArray, i: Int): Float =
        java.lang.Float.intBitsToFloat(readI32(buf, i))

    /** 把 −1..1 的归一化轴量转成协议用的整数。 */
    fun axisToWire(value: Float): Int =
        (value.coerceIn(-1f, 1f) * AXIS_MAX).toInt()

    /** 把协议整数还原成 −1..1。 */
    fun axisFromWire(value: Int): Float =
        value / AXIS_MAX
}

