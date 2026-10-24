package com.airyoke.link

import org.junit.Assert.*
import org.junit.Test

class AuxControlsTest {
    @Test fun `断开请求使用独立位且不覆盖其他控制`() {
        val c = AuxControls(true, true, false, 1f, 4, autopilotDisconnect = true)
        assertEquals(0x6F40, c.buttons)
        assertEquals(0, c.buttons and 0x08)
        assertEquals(5119, c.aux)
        val s = Protocol.State(1, 4, 0, 0, 0, 27851, c.buttons, c.aux, 42)
        val bytes = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(s, bytes)
        assertEquals(s, Protocol.decodeState(bytes, bytes.size))
        assertEquals(0x6F00, c.copy(autopilotDisconnect = false).buttons)
    }

    @Test fun `桥接能力位可以回传且旧遥测不会误认支持`() {
        val old = Protocol.Telemetry(1, 100f, 200f, 0f, 0f, 0f, 0f, true, 4, true, 100, .85f, 1f, true, 7)
        val bytes = ByteArray(Protocol.TELEMETRY_SIZE)
        Protocol.encodeTelemetry(old, bytes)
        assertEquals(0, Protocol.decodeTelemetry(bytes, bytes.size)!!.systemFlags and Protocol.SYSTEM_AP_DISCONNECT_SUPPORTED)
        val supported = old.copy(systemFlags = 15)
        Protocol.encodeTelemetry(supported, bytes)
        assertEquals(supported, Protocol.decodeTelemetry(bytes, bytes.size))
    }
    @Test fun `启动不会误发收板收襟翼或解除驻车`() {
        assertEquals(0, AuxControls().buttons)
    }
    @Test fun `同时操作不会覆盖邻近字段`() {
        val c = AuxControls(true, true, false, 1f, 4)
        assertEquals(0x6F00, c.buttons)
        assertEquals(5119, c.aux)
        val s = Protocol.State(1, 4, 0, 0, 0, 27851, c.buttons, c.aux, 42)
        val bytes = ByteArray(Protocol.STATE_SIZE)
        Protocol.encodeState(s, bytes)
        assertEquals(s, Protocol.decodeState(bytes, bytes.size))
    }
    @Test fun `遥测有效位与两个开关可往返`() {
        val t = Protocol.Telemetry(1, 100f, 200f, 0f, 0f, 0f, 0f, true, 4, false, 100, .85f, 1f, true, 7)
        val bytes = ByteArray(Protocol.TELEMETRY_SIZE)
        Protocol.encodeTelemetry(t, bytes)
        assertEquals(t, Protocol.decodeTelemetry(bytes, bytes.size))
    }
}
