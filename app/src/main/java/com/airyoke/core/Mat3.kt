package com.airyoke.core

import kotlin.math.abs

/**
 * 行主序 3x3 矩阵工具。
 *
 * 存储顺序与 Android [android.hardware.SensorManager] 的旋转矩阵一致：
 * ```
 * | m[0] m[1] m[2] |
 * | m[3] m[4] m[5] |
 * | m[6] m[7] m[8] |
 * ```
 *
 * 纯 Kotlin，不依赖 Android，因此可以在 JVM 上直接跑单元测试。
 */
object Mat3 {

    const val SIZE = 9

    /** 单位矩阵。 */
    fun identity(): FloatArray = floatArrayOf(
        1f, 0f, 0f,
        0f, 1f, 0f,
        0f, 0f, 1f,
    )

    /**
     * 矩阵乘法 `result = a · b`。
     *
     * @param result 可传入复用的数组以避免每帧分配；省略则新建。
     */
    fun multiply(a: FloatArray, b: FloatArray, result: FloatArray = FloatArray(SIZE)): FloatArray {
        require(a.size == SIZE && b.size == SIZE) { "Mat3 需要 9 元素数组" }
        // 先把结果算到局部变量，这样 result 与 a 或 b 是同一个数组也不会算坏
        val m00 = a[0] * b[0] + a[1] * b[3] + a[2] * b[6]
        val m01 = a[0] * b[1] + a[1] * b[4] + a[2] * b[7]
        val m02 = a[0] * b[2] + a[1] * b[5] + a[2] * b[8]

        val m10 = a[3] * b[0] + a[4] * b[3] + a[5] * b[6]
        val m11 = a[3] * b[1] + a[4] * b[4] + a[5] * b[7]
        val m12 = a[3] * b[2] + a[4] * b[5] + a[5] * b[8]

        val m20 = a[6] * b[0] + a[7] * b[3] + a[8] * b[6]
        val m21 = a[6] * b[1] + a[7] * b[4] + a[8] * b[7]
        val m22 = a[6] * b[2] + a[7] * b[5] + a[8] * b[8]

        result[0] = m00; result[1] = m01; result[2] = m02
        result[3] = m10; result[4] = m11; result[5] = m12
        result[6] = m20; result[7] = m21; result[8] = m22
        return result
    }

    /**
     * 转置。允许 `out` 与 `m` 是同一个数组。
     *
     * 旋转矩阵是正交矩阵，所以转置就是逆 —— 这正是"把姿态换算到参考系"所需要的。
     */
    fun transpose(m: FloatArray, out: FloatArray = FloatArray(SIZE)): FloatArray {
        require(m.size == SIZE) { "Mat3 需要 9 元素数组" }
        val a = m[0]; val b = m[1]; val c = m[2]
        val d = m[3]; val e = m[4]; val f = m[5]
        val g = m[6]; val h = m[7]; val i = m[8]

        out[0] = a; out[1] = d; out[2] = g
        out[3] = b; out[4] = e; out[5] = h
        out[6] = c; out[7] = f; out[8] = i
        return out
    }

    /** 判断是否接近单位矩阵，用于测试与校准状态判定。 */
    fun isIdentity(m: FloatArray, eps: Float = 1e-4f): Boolean {
        for (i in 0 until SIZE) {
            val expected = if (i == 0 || i == 4 || i == 8) 1f else 0f
            if (abs(m[i] - expected) > eps) return false
        }
        return true
    }
}
