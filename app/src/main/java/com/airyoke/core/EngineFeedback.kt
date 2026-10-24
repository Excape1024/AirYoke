package com.airyoke.core

import com.airyoke.link.Protocol

/** 只接受同机模且未过期的真实回读；杆位、N1、控制可用性各自判定。 */
object EngineFeedback {
    fun fresh(t: Protocol.Telemetry?, aircraft: Int, elapsedMs: Long, flag: Int): Boolean =
        t != null && t.aircraft == aircraft && elapsedMs in 0..499 &&
            t.engineAgeMs.toLong() + elapsedMs < 500 &&
            t.engineFlags and Protocol.ENGINE_MODEL_MISMATCH == 0 && t.engineFlags and flag != 0

    fun leversValid(t: Protocol.Telemetry?, aircraft: Int, elapsedMs: Long): Boolean =
        fresh(t, aircraft, elapsedMs, Protocol.ENGINE_LEVERS_VALID) &&
            t!!.throttlePos.isFinite() && t.throttlePos in -1f..1f &&
            t.throttlePos2.isFinite() && t.throttlePos2 in -1f..1f

    fun n1Valid(t: Protocol.Telemetry?, aircraft: Int, elapsedMs: Long): Boolean =
        fresh(t, aircraft, elapsedMs, Protocol.ENGINE_N1_VALID) &&
            t!!.n1Left.isFinite() && t.n1Left in 0f..120f && t.n1Right.isFinite() && t.n1Right in 0f..120f
}
