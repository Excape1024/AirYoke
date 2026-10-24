package com.airyoke.core

/** 断开请求最多发送 800 毫秒；只有请求之后的新反馈才能确认成功。 */
class AutopilotDisconnectRequest {
    enum class Status { IDLE, WAITING, CONFIRMED, UNCONFIRMED }
    var status = Status.IDLE
        private set
    private var until = 0L
    private var baselineSeq = 0

    fun request(now: Long, telemetrySeq: Int) {
        until = now + 800
        baselineSeq = telemetrySeq
        status = Status.WAITING
    }

    fun shouldSend(now: Long): Boolean = status == Status.WAITING && now < until

    fun update(now: Long, available: Boolean, telemetrySeq: Int?, autopilotOn: Boolean?) {
        if (status != Status.WAITING && status != Status.UNCONFIRMED) return
        if (!available) {
            cancel()
        } else if (telemetrySeq != null && telemetrySeq != baselineSeq && autopilotOn == false) {
            status = Status.CONFIRMED
        } else if (now >= until) {
            status = Status.UNCONFIRMED
        }
    }

    fun cancel() {
        until = 0
        if (status == Status.WAITING) status = Status.UNCONFIRMED
    }
}
