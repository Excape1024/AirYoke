package com.airyoke.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 校准逻辑的单元测试 —— 对应验收标准 1.1 / 1.4 / 1.5。
 */
class CalibrationTest {

    private val tolerance = 0.05f

    @Test
    fun `初始状态未校准`() {
        val calibration = Calibration()
        assertFalse(calibration.isCalibrated)
        assertNull(calibration.save())
    }

    @Test
    fun `校准后当前姿态读数为零`() {
        // 要求 1.1：以任意自然握姿校准后，输出立刻归零
        val calibration = Calibration()
        val grip = RotationFixtures.compose(RotationFixtures.rotZ(28f), RotationFixtures.rotX(9f))

        calibration.setReference(grip)
        assertTrue(calibration.isCalibrated)

        val axes = Attitude.toControlAxes(calibration.eulerFor(grip))
        assertEquals("副翼", 0f, axes.aileronDeg, tolerance)
        assertEquals("升降舵", 0f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `校准后偏离零点才产生输出`() {
        val calibration = Calibration()
        val grip = RotationFixtures.rotZ(-40f)
        calibration.setReference(grip)

        val moved = Mat3.multiply(grip, RotationFixtures.rotY(15f))
        val axes = Attitude.toControlAxes(calibration.eulerFor(moved))

        assertEquals("升降舵", 15f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `飞行中微调 把当前姿态设为新零点`() {
        // 要求 1.4：长时间飞行后漂移了，一键把当前姿态设为零点
        val calibration = Calibration()
        calibration.setReference(Mat3.identity())

        // 飞机飞了一会儿，手持姿态漂了 12°
        val drifted = RotationFixtures.rotX(12f)
        assertEquals(
            "微调前应有读数",
            12f,
            kotlin.math.abs(Attitude.toControlAxes(calibration.eulerFor(drifted)).aileronDeg),
            tolerance,
        )

        calibration.setReference(drifted)
        val axes = Attitude.toControlAxes(calibration.eulerFor(drifted))
        assertEquals("微调后副翼归零", 0f, axes.aileronDeg, tolerance)
        assertEquals("微调后升降舵归零", 0f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `校准可被清除`() {
        val calibration = Calibration()
        calibration.setReference(RotationFixtures.rotZ(50f))
        assertTrue(calibration.isCalibrated)

        calibration.clear()
        assertFalse(calibration.isCalibrated)
        assertNull(calibration.save())
    }

    // ---------- 要求 1.5：持久化 ----------

    @Test
    fun `校准可保存并恢复`() {
        val original = Calibration()
        val grip = RotationFixtures.compose(RotationFixtures.rotZ(17f), RotationFixtures.rotY(6f))
        original.setReference(grip)

        // 模拟杀进程重开
        val restored = Calibration()
        restored.restore(original.save())

        assertTrue(restored.isCalibrated)
        val axes = Attitude.toControlAxes(restored.eulerFor(grip))
        assertEquals("副翼", 0f, axes.aileronDeg, tolerance)
        assertEquals("升降舵", 0f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `恢复损坏的数据时退化为未校准`() {
        val calibration = Calibration()
        calibration.restore(floatArrayOf(1f, 2f, 3f)) // 长度不对
        assertFalse("长度不对应视为未校准", calibration.isCalibrated)

        calibration.restore(null)
        assertFalse(calibration.isCalibrated)
    }

    @Test
    fun `保存的是副本 外部修改不影响内部状态`() {
        val calibration = Calibration()
        val grip = RotationFixtures.rotX(20f)
        calibration.setReference(grip)

        val exported = calibration.save()!!
        exported[0] = 999f
        // 注意：JUnit 4 的 assertArrayEquals 对 float[] 没有无容差重载，必须给 delta
        assertArrayEquals(grip, calibration.save()!!, 1e-6f)

        grip[0] = 888f
        assertArrayEquals(RotationFixtures.rotX(20f), calibration.save()!!, 1e-6f)
    }

    @Test
    fun `传入的矩阵会被复制 调用方后续修改不影响校准`() {
        val calibration = Calibration()
        val matrix = RotationFixtures.rotY(25f)
        calibration.setReference(matrix)

        matrix[4] = 123f // 调用方复用同一个数组（传感器回调常这么做）
        assertEquals(1f, calibration.save()!![4], tolerance)
    }
}
