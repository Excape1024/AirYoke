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
        feedbackFresh = fresh && value != null && value.isFinite() && value in -1f..1f
        if (feedbackFresh) actual = value
    }

    fun takeOver(): Boolean {
        if (!feedbackFresh || actual == null) return false
        request(actual!!)
        return true
    }

    fun reset() {
        target = 0f
        actual = null
        feedbackFresh = false
    }
}
