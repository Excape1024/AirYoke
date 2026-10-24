package com.airyoke.core

import com.airyoke.link.Protocol
import org.junit.Assert.*
import org.junit.Test

class EngineFeedbackTest {
    private val t = Protocol.Telemetry(1, 0f, 0f, 0f, 0f, 0f, 0f, false, 0, false, 100,
        25f / 45, 0f, true, 1, 2, 15, 35f / 45, 83f, 84f, 20)

    @Test fun `自动推力改变N1不改变真实杆位`() {
        val state = ThrottleState()
        state.receive(t.throttlePos, EngineFeedback.leversValid(t, 2, 0))
        val next = t.copy(n1Left = 50f, n1Right = 51f)
        state.receive(next.throttlePos, EngineFeedback.leversValid(next, 2, 0))
        assertEquals(25f / 45, state.actual!!, 0f)
        assertTrue(EngineFeedback.n1Valid(next, 2, 0))
    }

    @Test fun `机模不匹配过期和非法值不得伪装成实时反馈`() {
        assertFalse(EngineFeedback.leversValid(t, 1, 0))
        assertFalse(EngineFeedback.leversValid(t.copy(engineFlags = 31), 2, 0))
        assertFalse(EngineFeedback.leversValid(t, 2, 480))
        assertFalse(EngineFeedback.leversValid(t.copy(throttlePos2 = Float.NaN), 2, 0))
        assertFalse(EngineFeedback.n1Valid(t.copy(n1Right = 999f), 2, 0))
        assertTrue(EngineFeedback.leversValid(t.copy(n1Right = Float.NaN), 2, 0))
        assertFalse(EngineFeedback.n1Valid(t.copy(engineFlags = 1), 2, 0))
    }

    @Test fun `切机清除旧状态且禁止使用过期杆位接管`() {
        val state = ThrottleState()
        state.receive(.5f, true)
        assertTrue(state.takeOver())
        state.receive(.6f, false)
        assertFalse(state.takeOver())
        state.reset()
        assertNull(state.actual); assertFalse(state.feedbackFresh); assertFalse(state.takeOver())
    }

    @Test fun `反推回读保留负杆位`() {
        val state = ThrottleState()
        state.receive(-.3f, true)
        assertEquals(-.3f, state.actual!!, 0f)
        state.receive(1.1f, true)
        assertFalse(state.feedbackFresh)
        assertEquals(-.3f, state.actual!!, 0f)
    }
}
