package com.airyoke.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AxisMapperTest {

    private val tolerance = 1e-4f

    @Test
    fun `线性映射 无死区无曲线`() {
        val mapping = AxisMapping(deadzoneDeg = 0f, maxAngleDeg = 20f, expo = 0f)

        assertEquals(0f, mapping.apply(0f), tolerance)
        assertEquals(0.5f, mapping.apply(10f), tolerance)
        assertEquals(1f, mapping.apply(20f), tolerance)
        assertEquals(-0.5f, mapping.apply(-10f), tolerance)
        assertEquals(-1f, mapping.apply(-20f), tolerance)
    }

    @Test
    fun `死区内输出恒为零`() {
        val mapping = AxisMapping(deadzoneDeg = 3f, maxAngleDeg = 25f, expo = 0f)

        for (angle in listOf(-3f, -1.5f, 0f, 1.5f, 3f)) {
            assertEquals("角度 $angle 应落在死区内", 0f, mapping.apply(angle), tolerance)
            assertTrue(mapping.isInDeadzone(angle))
        }
        assertFalse(mapping.isInDeadzone(3.1f))
    }

    @Test
    fun `死区外重新归一化 满舵角仍精确对应满输出`() {
        // 如果只是"减掉死区"，最大输出就永远到不了 1.0，手感上表现为舵量不够。
        // 这条测试锁住"重新归一化"这个行为。
        val mapping = AxisMapping(deadzoneDeg = 5f, maxAngleDeg = 25f, expo = 0f)

        assertEquals("满舵角必须给出 1.0", 1f, mapping.apply(25f), tolerance)
        assertEquals("超过满舵角限幅到 1.0", 1f, mapping.apply(60f), tolerance)
        assertEquals("刚出死区时接近 0", 0f, mapping.apply(5.01f), 0.01f)
    }

    @Test
    fun `expo 保持端点不变 只改变中段形状`() {
        val linear = AxisMapping(deadzoneDeg = 0f, maxAngleDeg = 25f, expo = 0f)
        val curved = AxisMapping(deadzoneDeg = 0f, maxAngleDeg = 25f, expo = 0.6f)

        assertEquals("端点 0", linear.apply(0f), curved.apply(0f), tolerance)
        assertEquals("满舵端点", linear.apply(25f), curved.apply(25f), tolerance)
        assertEquals("负满舵端点", linear.apply(-25f), curved.apply(-25f), tolerance)
    }

    @Test
    fun `expo 让中位附近更细腻`() {
        val linear = AxisMapping(deadzoneDeg = 0f, maxAngleDeg = 25f, expo = 0f)
        val curved = AxisMapping(deadzoneDeg = 0f, maxAngleDeg = 25f, expo = 0.7f)

        val halfInput = 12.5f
        assertTrue(
            "expo 后中位输出应小于线性",
            kotlin.math.abs(curved.apply(halfInput)) < kotlin.math.abs(linear.apply(halfInput)),
        )
    }

    @Test
    fun `映射在整个行程上单调不减`() {
        // 单调性是安全要求：手感可以变，但不能出现"推得更多反而舵量更小"
        val mapping = AxisMapping(deadzoneDeg = 2f, maxAngleDeg = 30f, expo = 0.5f)

        var previous = -1.1f
        var angle = -45f
        while (angle <= 45f) {
            val output = mapping.apply(angle)
            assertTrue("角度 $angle 处出现回退", output >= previous - tolerance)
            previous = output
            angle += 0.5f
        }
    }

    @Test
    fun `反转改变符号但不改变幅度`() {
        val normal = AxisMapping(deadzoneDeg = 2f, maxAngleDeg = 25f, expo = 0.3f, inverted = false)
        val inverted = normal.copy(inverted = true)

        for (angle in listOf(-30f, -12f, -5f, 0f, 5f, 12f, 30f)) {
            assertEquals(
                "角度 $angle",
                -normal.apply(angle),
                inverted.apply(angle),
                tolerance,
            )
        }
    }

    @Test
    fun `满舵角小于死区时安全返回零`() {
        // 参数下发错误时不该产生除零或 NaN，直接退化为无输出
        val broken = AxisMapping(deadzoneDeg = 30f, maxAngleDeg = 25f)
        assertEquals(0f, broken.apply(20f), tolerance)
        assertEquals(0f, broken.apply(-20f), tolerance)
    }

    @Test
    fun `输出永远落在负一到正一之间`() {
        val mapping = AxisMapping(deadzoneDeg = 2f, maxAngleDeg = 25f, expo = 0.8f)

        var angle = -180f
        while (angle <= 180f) {
            val output = mapping.apply(angle)
            assertTrue("角度 $angle 输出越界: $output", output in -1f..1f)
            angle += 1f
        }
    }
}
