package com.airyoke.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 端到端管线测试：姿态 → 校准 → 映射 → 四轴输出。
 *
 * 其中前两条是**实机测定的回归守卫**。默认反转值来自 2026-09-27 的实测：
 * 手机横握当驾驶盘时，右压盘得到负的原始副翼读数、前推盘得到正的原始升降舵读数，
 * 两者都与飞行语义相反，故出厂默认两个轴都反转。
 *
 * 如果哪天有人"顺手"把默认值改回去，这两条会立刻失败。
 */
class ControlPipelineTest {

    private val tolerance = 1e-3f

    /** 实测：右压盘（绕设备 X 轴正向转）应输出**正**副翼，即右滚。 */
    @Test
    fun `出厂默认下 右压盘给出正副翼`() {
        val pipeline = ControlPipeline()
        pipeline.zeroHere(Mat3.identity())

        val output = pipeline.update(RotationFixtures.rotX(20f))

        assertTrue(
            "右压盘应给出正副翼（右滚），实际 ${output.aileron}",
            output.aileron > 0f,
        )
    }

    /** 实测：前推盘（绕设备 Y 轴正向转）应输出**负**升降舵，即低头。 */
    @Test
    fun `出厂默认下 前推盘给出负升降舵`() {
        val pipeline = ControlPipeline()
        pipeline.zeroHere(Mat3.identity())

        val output = pipeline.update(RotationFixtures.rotY(20f))

        assertTrue(
            "前推盘应给出负升降舵（低头），实际 ${output.elevator}",
            output.elevator < 0f,
        )
    }

    @Test
    fun `两轴互不串扰`() {
        val pipeline = ControlPipeline()
        pipeline.zeroHere(Mat3.identity())

        val output = pipeline.update(RotationFixtures.rotX(20f))

        assertEquals("压盘不该带动升降舵", 0f, output.elevator, tolerance)
    }

    @Test
    fun `校准后当前姿态输出归零`() {
        val pipeline = ControlPipeline()
        val grip = RotationFixtures.compose(RotationFixtures.rotZ(31f), RotationFixtures.rotX(11f))
        pipeline.zeroHere(grip)

        val output = pipeline.update(grip)

        assertEquals("副翼", 0f, output.aileron, tolerance)
        assertEquals("升降舵", 0f, output.elevator, tolerance)
    }

    @Test
    fun `未校准时输出为零 不随手机乱跳`() {
        val pipeline = ControlPipeline()

        val output = pipeline.update(RotationFixtures.rotX(40f))

        assertEquals("副翼", 0f, output.aileron, tolerance)
        assertEquals("升降舵", 0f, output.elevator, tolerance)
    }

    @Test
    fun `死区内的微小晃动被吃掉`() {
        val pipeline = ControlPipeline()
        pipeline.zeroHere(Mat3.identity())

        // 出厂死区 2°，1.5° 的抖动应完全不出力
        val output = pipeline.update(RotationFixtures.rotX(1.5f))

        assertEquals("副翼应为零", 0f, output.aileron, tolerance)
    }

    @Test
    fun `方向舵与油门原样透传`() {
        val pipeline = ControlPipeline()
        pipeline.zeroHere(Mat3.identity())

        val output = pipeline.update(Mat3.identity(), rudder = -0.4f, throttle = 0.75f)

        assertEquals(-0.4f, output.rudder, tolerance)
        assertEquals(0.75f, output.throttle, tolerance)
    }

    @Test
    fun `方向舵与油门被限幅到正负一`() {
        val pipeline = ControlPipeline()
        pipeline.zeroHere(Mat3.identity())

        val output = pipeline.update(Mat3.identity(), rudder = -9f, throttle = 9f)

        assertEquals(-1f, output.rudder, tolerance)
        assertEquals(1f, output.throttle, tolerance)
    }
}
