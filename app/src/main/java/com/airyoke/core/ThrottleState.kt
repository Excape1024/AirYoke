package com.airyoke.core

/** 指令与反馈独立保存，位置回读绝不参与持续发送的目标计算。 */
class ThrottleState {
    var target = 0f
        private set
    var actual: Float? = null
        private set
    var feedbackFresh = false
        private set

    fun request(value: Float) {
        require(value.isFinite()) { "油门目标必须是有限数值" }
        target = value.coerceIn(-1f, 1f)
    }

    fun receive(value: Float?, fresh: Boolean) {
        feedbackFresh = fresh && value != null && value.isFinite()
        // 与现行桥接协议一致：只提供 0..1 的正推力杆位置，不推测反推位置。
        if (feedbackFresh) actual = value!!.coerceIn(0f, 1f)
    }

    fun takeOver() {
        // 以最近一次飞机位置开始接管；没有回读时保留原目标。
        request(actual ?: target.coerceAtLeast(0f))
    }
}
