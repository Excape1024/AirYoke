package com.airyoke.core

import org.junit.Assert.*
import org.junit.Test

class LeverMappingTest {
    private val map = listOf(-1f to 0f, 0f to .18f, .89f to .68f, .95f to .86f, 1f to 1f)
    @Test fun `视觉卡位映射到实际轴值`() {
        assertEquals(.68f, LeverMapping.interpolate(.89f, map), .0001f)
        assertEquals(.89f, LeverMapping.interpolate(.68f, map.map { it.second to it.first }), .0001f)
    }
    @Test fun `全行程往返包含反推与端点`() {
        for (n in -100..100) {
            val v = n / 100f
            val p = LeverMapping.interpolate(v, map)
            assertEquals(v, LeverMapping.interpolate(p, map.map { it.second to it.first }), .0001f)
        }
    }
}
