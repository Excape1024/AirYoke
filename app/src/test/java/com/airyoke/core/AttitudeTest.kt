package com.airyoke.core

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * 姿态解算的单元测试。
 *
 * 这段代码是整个 App 最容易接反的地方 —— 一旦副翼和升降舵对调，飞机在游戏里会
 * 做出"压盘就俯仰"的诡异动作，而那种 bug 靠肉眼盯着手机很难查。所以这里把每条
 * 轴的对应关系和符号都钉死。
 *
 * 对应的验收标准：
 *  - 1.1 校准后输出归零
 *  - 1.2 偏航不影响俯仰/滚转
 *  - 1.3 倾斜方向正确
 */
class AttitudeTest {

    private val tolerance = 0.05f

    // ---------- 要求 1.1：校准后归零 ----------

    @Test
    fun `姿态未变化时所有角为零`() {
        val r = RotationFixtures.compose(RotationFixtures.rotZ(33f), RotationFixtures.rotX(12f))
        val euler = Attitude.relative(r, r)

        assertEquals("azimuth", 0f, euler.azimuthDeg, tolerance)
        assertEquals("pitch", 0f, euler.pitchDeg, tolerance)
        assertEquals("roll", 0f, euler.rollDeg, tolerance)
    }

    @Test
    fun `未校准时输出为零`() {
        // 没有参考系时不该乱跳，应稳定显示"未校准、输出为零"
        val calibration = Calibration()
        val euler = calibration.eulerFor(RotationFixtures.rotX(45f))

        assertEquals(0f, euler.aileronOrPitch(), tolerance)
        assertEquals(0f, euler.elevatorOrRoll(), tolerance)
    }

    // ---------- 要求 1.3：轴对应关系与符号 ----------

    @Test
    fun `绕设备 X 轴压盘 只驱动副翼`() {
        // 手机横握当驾驶盘：绕设备 X（前后轴）转 = 像转方向盘一样压盘 = 副翼
        val ref = Mat3.identity()

        val euler = Attitude.relative(ref, RotationFixtures.rotX(30f))
        val axes = Attitude.toControlAxes(euler)

        // Android 约定下 pitch = asin(-R[7])，所以 +30° 的 rotX 得到 −30°
        assertEquals("副翼幅度应为 30°", -30f, axes.aileronDeg, tolerance)
        assertEquals("升降舵不应被带动", 0f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `绕设备 Y 轴推拉盘 只驱动升降舵`() {
        // 绕设备 Y（左右轴）转 = 推拉驾驶盘 = 升降舵
        val ref = Mat3.identity()

        val euler = Attitude.relative(ref, RotationFixtures.rotY(30f))
        val axes = Attitude.toControlAxes(euler)

        assertEquals("升降舵幅度应为 30°", 30f, axes.elevatorDeg, tolerance)
        assertEquals("副翼不应被带动", 0f, axes.aileronDeg, tolerance)
    }

    @Test
    fun `符号约定被钉死 改动 fromRotationMatrix 会立刻被发现`() {
        // 这两个符号来自 Android 框架自身的约定（pitch 与 azimuth 都取负）。
        // 符号错了靠 invert 选项也能救回来，但必须"知道"它错在哪，不能糊里糊涂。
        assertEquals(
            "rotX(+30) 的 pitch 应为 −30",
            -30f,
            Attitude.fromRotationMatrix(RotationFixtures.rotX(30f)).pitchDeg,
            tolerance,
        )
        assertEquals(
            "rotY(+30) 的 roll 应为 +30",
            30f,
            Attitude.fromRotationMatrix(RotationFixtures.rotY(30f)).rollDeg,
            tolerance,
        )
        assertEquals(
            "rotZ(+30) 的 azimuth 应为 −30",
            -30f,
            Attitude.fromRotationMatrix(RotationFixtures.rotZ(30f)).azimuthDeg,
            tolerance,
        )
    }

    // ---------- 要求 1.2：偏航不泄漏到俯仰/滚转 ----------

    @Test
    fun `水平扭转手机 不影响副翼与升降舵`() {
        // 磁力计在室内会抖动，偏航角本来就不可靠。
        // 我们索性不用它 —— 这条测试保证它不会偷偷渗进操纵轴。
        val ref = Mat3.identity()

        for (yawDeg in listOf(-170f, -90f, -45f, -10f, 10f, 45f, 90f, 170f)) {
            val euler = Attitude.relative(ref, RotationFixtures.rotZ(yawDeg))
            val axes = Attitude.toControlAxes(euler)

            assertEquals("偏航 ${yawDeg}° 时的副翼", 0f, axes.aileronDeg, tolerance)
            assertEquals("偏航 ${yawDeg}° 时的升降舵", 0f, axes.elevatorDeg, tolerance)
        }
    }

    // ---------- 参考系数学 ----------

    @Test
    fun `参考系不是单位矩阵时 相对姿态依然正确`() {
        // 用户以任意握姿校准：参考系是那个握姿，而不是"水平"
        val ref = RotationFixtures.compose(RotationFixtures.rotZ(45f), RotationFixtures.rotX(8f))
        val moved = Mat3.multiply(ref, RotationFixtures.rotY(20f))

        val axes = Attitude.toControlAxes(Attitude.relative(ref, moved))

        assertEquals("升降舵", 20f, axes.elevatorDeg, tolerance)
        assertEquals("副翼", 0f, axes.aileronDeg, tolerance)
    }

    @Test
    fun `在已旋转的参考系里继续压盘 副翼读数不受参考系影响`() {
        val ref = RotationFixtures.compose(RotationFixtures.rotZ(-70f), RotationFixtures.rotY(15f))
        val moved = Mat3.multiply(ref, RotationFixtures.rotX(22f))

        val axes = Attitude.toControlAxes(Attitude.relative(ref, moved))

        assertEquals("副翼", -22f, axes.aileronDeg, tolerance)
        assertEquals("升降舵", 0f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `同时压盘与推拉 两轴互不干扰`() {
        val ref = Mat3.identity()
        val moved = RotationFixtures.compose(
            RotationFixtures.rotX(-18f), // 压盘
            RotationFixtures.rotY(25f),  // 拉杆
        )

        val axes = Attitude.toControlAxes(Attitude.relative(ref, moved))

        assertEquals("副翼", 18f, axes.aileronDeg, tolerance)
        assertEquals("升降舵", 25f, axes.elevatorDeg, tolerance)
    }

    @Test
    fun `小角度输入下 输出近似线性`() {
        // 手握手机会有细微晃动，小角度必须稳定单调，不能跳
        val ref = Mat3.identity()
        var previous = -1f

        for (deg in 1..10) {
            val axes = Attitude.toControlAxes(
                Attitude.relative(ref, RotationFixtures.rotX(deg.toFloat())),
            )
            val magnitude = kotlin.math.abs(axes.aileronDeg)
            assertEquals("${deg}° 的幅度", deg.toFloat(), magnitude, tolerance)
            org.junit.Assert.assertTrue("幅度应随角度单调增", magnitude > previous)
            previous = magnitude
        }
    }

    // ---------- 小工具 ----------

    private fun Attitude.Euler.aileronOrPitch(): Float =
        Attitude.toControlAxes(this).aileronDeg

    private fun Attitude.Euler.elevatorOrRoll(): Float =
        Attitude.toControlAxes(this).elevatorDeg
}
