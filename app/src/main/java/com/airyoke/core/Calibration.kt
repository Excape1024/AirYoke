package com.airyoke.core

/**
 * 校准状态机（纯逻辑，不含 Android 依赖，可在 JVM 上单测）。
 *
 * 三级校准，对应计划里的要求 1：
 *
 *  1. **陀螺零偏**（可选）—— 手机静置数秒，取陀螺仪均值作零偏，降低漂移
 *  2. **水平姿态**（核心）—— 以自然握姿持机，记录当前旋转矩阵作参考系 `R_ref`
 *  3. **飞行中微调** —— 把当前姿态设为新零点，补偿长时间漂移
 *
 * 其中第 2、3 步本质是同一个操作（更新 `R_ref`），区别只在何时触发。
 */
class Calibration {

    /** 校准时刻的旋转矩阵。null 表示尚未校准。 */
    var reference: FloatArray? = null
        private set

    /** 是否已完成水平姿态校准。 */
    val isCalibrated: Boolean get() = reference != null

    /**
     * 以给定姿态作为新的零点（对应"校准水平姿态"与"飞行中微调"）。
     *
     * @param rotationMatrix 当前旋转矩阵（行主序 9 元素），会被复制保存
     */
    fun setReference(rotationMatrix: FloatArray) {
        require(rotationMatrix.size == Mat3.SIZE) { "需要 9 元素旋转矩阵" }
        reference = rotationMatrix.copyOf()
    }

    /** 清除校准，回到"未校准"状态。 */
    fun clear() {
        reference = null
    }

    /**
     * 计算给定姿态相对于零点的角度。
     *
     * 未校准时直接返回全 0 —— 这样界面上不会出现莫名其妙的乱跳，
     * 而是稳定地显示"未校准、输出为零"。
     */
    fun eulerFor(rotationMatrix: FloatArray): Attitude.Euler {
        val ref = reference ?: return Attitude.Euler(0f, 0f, 0f)
        return Attitude.relative(ref, rotationMatrix)
    }

    /** 恢复已持久化的校准（从 SharedPreferences 读出的 9 个 float）。 */
    fun restore(saved: FloatArray?) {
        reference = if (saved != null && saved.size == Mat3.SIZE) saved.copyOf() else null
    }

    /** 导出用于持久化的数组。 */
    fun save(): FloatArray? = reference?.copyOf()
}
