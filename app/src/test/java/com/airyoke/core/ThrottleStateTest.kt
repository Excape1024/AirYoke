package com.airyoke.core

import org.junit.Assert.*
import org.junit.Test

class ThrottleStateTest {
    @Test fun `首次回读之前不能把目标冒充实际位置`() {
        val state = ThrottleState()
        state.request(.95f)
        assertNull(state.actual)
        assertFalse(state.feedbackFresh)
        assertEquals(.95f, state.target, .0001f)
    }

    @Test fun `拖动和松手后持续回读都不能覆盖油门目标`() {
        val state = ThrottleState()
        state.receive(.2f, true)
        state.takeOver()
        state.request(.95f)
        // 模拟拖动期间的回读以及松手后的多次反馈。
        listOf(.3f, .5f, .7f, .9f, .95f).forEach {
            state.receive(it, true)
            assertEquals(it, state.actual!!, .0001f)
            assertEquals(.95f, state.target, .0001f)
        }
    }

    @Test fun `交还CL期间仍然展示飞机反馈且不篡改CL目标`() {
        val state = ThrottleState()
        state.request(1f)
        state.receive(1f, true)
        state.request(.89f)
        assertEquals(1f, state.actual!!, .0001f)
        state.receive(.6f, true)
        assertEquals(.6f, state.actual!!, .0001f)
        assertEquals(.89f, state.target, .0001f)
    }

    @Test fun `交还后重新接管以飞机位置为起点`() {
        val state = ThrottleState()
        state.request(.89f)
        state.receive(.45f, true)
        state.takeOver()
        assertEquals(.45f, state.target, .0001f)
    }

    @Test fun `反馈过期保留最后位置且恢复时跟随新反馈`() {
        val state = ThrottleState()
        state.request(.95f)
        state.receive(.4f, true)
        state.receive(.8f, false)
        assertFalse(state.feedbackFresh)
        assertEquals(.4f, state.actual!!, .0001f)
        assertEquals(.95f, state.target, .0001f)
        state.receive(.7f, true)
        assertTrue(state.feedbackFresh)
        assertEquals(.7f, state.actual!!, .0001f)
    }

    @Test fun `无效反馈不能污染最后位置和目标`() {
        val state = ThrottleState()
        state.request(.95f)
        state.receive(.4f, true)
        listOf(null, Float.NaN, Float.POSITIVE_INFINITY).forEach {
            state.receive(it, true)
            assertFalse(state.feedbackFresh)
            assertEquals(.4f, state.actual!!, .0001f)
            assertEquals(.95f, state.target, .0001f)
        }
    }

    @Test fun `反推目标和释放到慢车都不伪造实际位置`() {
        val state = ThrottleState()
        state.receive(.1f, true)
        state.request(-1f)
        assertEquals(-1f, state.target, .0001f)
        assertEquals(.1f, state.actual!!, .0001f)
        state.request(0f)
        assertEquals(0f, state.target, .0001f)
        assertEquals(.1f, state.actual!!, .0001f)
    }

    @Test(expected = IllegalArgumentException::class)
    fun `拒绝非有限油门指令`() {
        ThrottleState().request(Float.NaN)
    }
}
