package com.airyoke.core

import kotlin.math.asin
import kotlin.math.atan2

/**
 * 体感两轴：副翼与升降舵。
 *
 * 方向舵和油门**不在这里** —— 它们走屏幕滑条（见计划）。这里只有手机能体感的那两轴。
 */
data class ControlAxes(
    /** 副翼。正 = 右压盘（右滚转）。 */
    val aileronDeg: Float,
    /** 升降舵。正 = 抬头（拉杆）。 */
    val elevatorDeg: Float,
)

/**
 * 姿态解算：把"当前姿态"表达为"相对于校准参考姿态"的角度。
 *
 * 核心式子是 `D = R_refᵀ · R_now`：
 *  - `R` 是 Android `getRotationMatrixFromVector` 给出的旋转矩阵，把设备坐标映射到世界坐标
 *  - 于是 `D` 把"当前设备坐标"映射回"校准时刻的设备坐标"
 *  - 没校准或没动过时 `D = I`，所有角为 0 —— 这正是"校准水平姿态"要的效果
 *
 * 一套式子同时解决两件事：**任意握姿**（参考系就是那次握姿）和**校准归零**。
 *
 * 这里刻意不调 [android.hardware.SensorManager.getOrientation]，而是自己按同样的约定
 * 实现欧拉角提取，好让整段数学能在 JVM 上跑单元测试 —— 这段代码最容易接反，必须测。
 */
object Attitude {

    /** 欧拉角，单位为度。约定与 Android 框架一致。 */
    data class Euler(
        /** 绕垂直轴（设备 Z）的转角。本 App 不使用。 */
        val azimuthDeg: Float,
        /** 绕设备 X 轴（横握时指向正前方）的转角。 */
        val pitchDeg: Float,
        /** 绕设备 Y 轴（横握时指向左右）的转角。 */
        val rollDeg: Float,
    )

    /**
     * 计算相对姿态角。
     *
     * @param ref 校准时刻的旋转矩阵（行主序 9 元素）
     * @param now 当前旋转矩阵（行主序 9 元素）
     */
    fun relative(ref: FloatArray, now: FloatArray): Euler =
        fromRotationMatrix(Mat3.multiply(Mat3.transpose(ref), now))

    /**
     * 从旋转矩阵提取欧拉角。
     *
     * 用的就是 Android 框架内部那套式子：
     * ```
     * azimuth = atan2( R[1],  R[4])
     * pitch   = asin (-R[7])
     * roll    = atan2(-R[6],  R[8])
     * ```
     * 注意 pitch 和 azimuth 都带负号 —— 这是 Android 的既有约定，符号问题交给
     * 每轴的 `inverted` 选项去纠，不在这里"顺手改掉"，否则会和框架行为对不上。
     */
    fun fromRotationMatrix(m: FloatArray): Euler = Euler(
        azimuthDeg = Math.toDegrees(atan2(m[1].toDouble(), m[4].toDouble())).toFloat(),
        pitchDeg = Math.toDegrees(asin((-m[7]).toDouble().coerceIn(-1.0, 1.0))).toFloat(),
        rollDeg = Math.toDegrees(atan2((-m[6]).toDouble(), m[8].toDouble())).toFloat(),
    )

    /**
     * 把欧拉角映射到飞机操纵轴。
     *
     * 手机横握当驾驶盘时，设备坐标系的 X 轴指向正前方、Y 轴指向左右、Z 轴朝上：
     *
     * | 玩家动作             | 物理转轴        | 取自        | 映射到   |
     * |----------------------|-----------------|-------------|----------|
     * | 像转方向盘一样左右压盘 | 绕设备 X（前后） | `pitchDeg`  | 副翼     |
     * | 推/拉盘              | 绕设备 Y（左右） | `rollDeg`   | 升降舵   |
     *
     * 偏航（绕设备 Z）**刻意丢弃** —— 方向舵不走传感器，且室内磁力计的方位角不可靠。
     */
    fun toControlAxes(euler: Euler): ControlAxes = ControlAxes(
        aileronDeg = euler.pitchDeg,
        elevatorDeg = euler.rollDeg,
    )
}
