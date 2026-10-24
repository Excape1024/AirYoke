package com.airyoke.core

import org.junit.Assert.*
import org.junit.Test

class AutopilotDisconnectRequestTest {
    @Test fun `启动时不发送断开请求`() {
        val request = AutopilotDisconnectRequest()
        assertFalse(request.shouldSend(0))
    }

    @Test fun `请求只在800毫秒内重发`() {
        val request = AutopilotDisconnectRequest()
        request.request(1000, 10)
        assertTrue(request.shouldSend(1000))
        assertTrue(request.shouldSend(1799))
        assertFalse(request.shouldSend(1800))
        request.update(1800, true, 11, true)
        assertEquals(AutopilotDisconnectRequest.Status.UNCONFIRMED, request.status)
    }

    @Test fun `旧的关闭回读不能确认刚发出的请求`() {
        val request = AutopilotDisconnectRequest()
        request.request(1000, 10)
        request.update(1020, true, 10, false)
        assertEquals(AutopilotDisconnectRequest.Status.WAITING, request.status)
        assertTrue(request.shouldSend(1020))
        request.update(1100, true, 11, false)
        assertEquals(AutopilotDisconnectRequest.Status.CONFIRMED, request.status)
        assertFalse(request.shouldSend(1100))
    }

    @Test fun `丢失连接取消重发且恢复时不重放`() {
        val request = AutopilotDisconnectRequest()
        request.request(1000, 10)
        request.update(1020, false, 10, true)
        request.update(1100, true, 11, true)
        assertFalse(request.shouldSend(1100))
        assertEquals(AutopilotDisconnectRequest.Status.UNCONFIRMED, request.status)
    }

    @Test fun `切到后台取消请求`() {
        val request = AutopilotDisconnectRequest()
        request.request(1000, 10)
        request.cancel()
        assertFalse(request.shouldSend(1020))
    }

    @Test fun `重复点击允许新的有限重试`() {
        val request = AutopilotDisconnectRequest()
        request.request(1000, 10)
        request.update(1800, true, 11, true)
        request.request(2000, 11)
        assertTrue(request.shouldSend(2000))
        request.update(2100, true, 12, false)
        assertFalse(request.shouldSend(2100))
    }
}
