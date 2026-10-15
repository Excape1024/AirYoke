package com.airyoke.core

/** 屏幕行程与机型轴值分开，给靠近满推力的几个卡位留出可触摸的距离。 */
object LeverMapping {
    fun interpolate(value: Float, points: List<Pair<Float, Float>>): Float {
        require(points.size >= 2)
        if (value <= points.first().first) return points.first().second
        if (value >= points.last().first) return points.last().second
        val i = points.indexOfFirst { it.first >= value }
        val (x0, y0) = points[i - 1]
        val (x1, y1) = points[i]
        return y0 + (value - x0) / (x1 - x0) * (y1 - y0)
    }
}
