package com.airyoke.core

import kotlin.math.abs

/** 在行程空间吸附；离开门槛大于进入门槛，避免卡位边缘抖动。 */
class DetentSnapper {
    var capture = 0.03f
    var release = 0.05f
    var locked: Float? = null
        private set

    fun reset() { locked = null }

    fun apply(position: Float, stops: List<Float>): Float {
        val p = position.coerceIn(0f, 1f)
        // 相邻卡位很近时缩小磁区，确保始终能从 FLX 推到 TOGA。
        fun gap(stop: Float) = stops.filter { it != stop }.minOfOrNull { abs(it - stop) } ?: 1f
        locked?.let { if (abs(p - it) <= minOf(release, gap(it) * .65f)) return it }
        locked = stops.minByOrNull { abs(p - it) }?.takeIf { abs(p - it) <= minOf(capture, gap(it) * .4f) }
        return locked ?: p
    }
}
