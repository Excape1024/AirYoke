package com.airyoke.core

import kotlin.math.abs

/**
 * 单轴映射管线的参数。
 *
 * 管线顺序：
 * ```
 * 原始角度(°) → 中心死区 → 归一化到满舵角 → Expo 曲线 → 反转
 * ```
 * 输出归一化到 `[-1, 1]`。
 *
 * 纯 Kotlin 数据类，可在 JVM 上单元测试；具体数值由 App 的设置界面或 PC 调参面板下发。
 */
data class AxisMapping(
    /** 中心死区，单位度。死区内的输出恒为 0，用来吃掉手持抖动。 */
    val deadzoneDeg: Float = 2f,
    /** 满舵角，单位度。倾斜到这个角度即输出 ±1。越小手感越"灵敏"。 */
    val maxAngleDeg: Float = 25f,
    /** Expo 强度 0..1。0 = 线性；越大中位附近越细腻、两端越陡。 */
    val expo: Float = 0.3f,
    /** 反转该轴，用来适配不同握姿。 */
    val inverted: Boolean = false,
) {

    /**
     * 把一个角度映射为 `[-1, 1]` 的归一化输出。
     *
     * 死区外**重新归一化**（而不是直接减掉死区），这样满舵角仍然精确对应输出 ±1，
     * 否则死区会让最大输出永远到不了满舵，手感上表现为"舵量不够"。
     */
    fun apply(angleDeg: Float): Float {
        val usable = maxAngleDeg - deadzoneDeg
        if (usable <= 0f) return 0f

        val magnitude = abs(angleDeg)
        val normalized = ((magnitude - deadzoneDeg) / usable).coerceIn(0f, 1f)

        // Expo: y = (1-k)·x + k·x³
        val k = expo.coerceIn(0f, 1f)
        val curved = (1f - k) * normalized + k * normalized * normalized * normalized

        val signed = if (angleDeg < 0f) -curved else curved
        return if (inverted) -signed else signed
    }

    /**
     * 判断是否落在死区内。UI 上用来显示"已回中"。
     */
    fun isInDeadzone(angleDeg: Float): Boolean = abs(angleDeg) <= deadzoneDeg
}

/**
 * 四轴输出的归一化结果，各轴取值 −1..1。
 *
 * **这是本 App 对外的轴语义约定** —— 阶段 4 的桥接程序按这个把值映射到 SimConnect，
 * 两边必须一致，所以写在这里而不是留在口头：
 *
 * | 轴 | 正方向 | 负方向 |
 * |---|---|---|
 * | [aileron]  | 右滚（右翼下沉） | 左滚 |
 * | [elevator] | **抬头（拉杆）** | 低头（推杆） |
 * | [rudder]   | 右偏航（机头向右） | 左偏航 |
 * | [throttle] | 满推力（+1） | 最大反推（−1） |
 *
 * **油门 0 表示慢车**，不是"最小值"。慢车以下（负值）是反推段，
 * 与真机推力杆一致：慢车是个卡位，反推是它下面的一小段行程。
 * 这样映射到 SimConnect 的推力轴时也是直通的（该轴本身就支持负值反推）。
 *
 * 副翼与升降舵来自传感器；方向舵与油门来自屏幕滑条，由调用方直接填入。
 */
data class ControlOutput(
    /** 副翼 −1..1 */
    val aileron: Float,
    /** 升降舵 −1..1。正 = 抬头。 */
    val elevator: Float,
    /** 方向舵 −1..1 */
    val rudder: Float,
    /** 油门 −1..1 */
    val throttle: Float,
)
