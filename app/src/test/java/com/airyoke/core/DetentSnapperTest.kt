package com.airyoke.core

import org.junit.Assert.*
import org.junit.Test

class DetentSnapperTest {
    @Test fun `进入吸附后较小抖动不会离开`() {
        val s = DetentSnapper()
        assertEquals(.6f, s.apply(.58f, listOf(0f, .6f, 1f)), .0001f)
        assertEquals(.6f, s.apply(.64f, listOf(0f, .6f, 1f)), .0001f)
        assertEquals(.67f, s.apply(.67f, listOf(0f, .6f, 1f)), .0001f)
    }
    @Test fun `紧邻的最高档始终可达`() {
        val s = DetentSnapper().apply { capture = .04f; release = .065f }
        val stops = listOf(.18f, .877f, .959f, 1f)
        assertEquals(.959f, s.apply(.959f, stops), .0001f)
        assertEquals(1f, s.apply(1f, stops), .0001f)
    }
    @Test fun `复位后不保留上次吸附`() {
        val s = DetentSnapper()
        s.apply(.5f, listOf(.5f)); s.reset()
        assertEquals(.54f, s.apply(.54f, listOf(.5f)), .0001f)
    }
}
