package com.airyoke.core

import kotlin.math.cos
import kotlin.math.sin

/**
 * 测试用的旋转矩阵构造函数。
 *
 * 约定与 Android 一致：行主序 3x3，把设备坐标映射到世界坐标。
 */
object RotationFixtures {

    /** 绕设备 X 轴（横握时指向正前方）旋转。 */
    fun rotX(deg: Float): FloatArray {
        val r = Math.toRadians(deg.toDouble())
        val c = cos(r).toFloat()
        val s = sin(r).toFloat()
        return floatArrayOf(
            1f, 0f, 0f,
            0f, c, -s,
            0f, s, c,
        )
    }

    /** 绕设备 Y 轴（横握时指向左右）旋转。 */
    fun rotY(deg: Float): FloatArray {
        val r = Math.toRadians(deg.toDouble())
        val c = cos(r).toFloat()
        val s = sin(r).toFloat()
        return floatArrayOf(
            c, 0f, s,
            0f, 1f, 0f,
            -s, 0f, c,
        )
    }

    /** 绕设备 Z 轴（垂直轴）旋转。 */
    fun rotZ(deg: Float): FloatArray {
        val r = Math.toRadians(deg.toDouble())
        val c = cos(r).toFloat()
        val s = sin(r).toFloat()
        return floatArrayOf(
            c, -s, 0f,
            s, c, 0f,
            0f, 0f, 1f,
        )
    }

    /** 依次施加多个旋转：`compose(rotZ(45f), rotX(30f))` = Rz · Rx。 */
    fun compose(vararg matrices: FloatArray): FloatArray {
        var acc = Mat3.identity()
        for (m in matrices) acc = Mat3.multiply(acc, m)
        return acc
    }
}
