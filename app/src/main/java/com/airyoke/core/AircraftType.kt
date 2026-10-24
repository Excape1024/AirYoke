package com.airyoke.core

/** 编号必须与桥接端一致；不把旧 Asobo 原版混入 V2 选项。 */
enum class AircraftType(val wireId: Int, val title: String) {
    INIBUILDS(1, "MSFS 2020 · A320neo V2"),
    FLYBYWIRE(2, "FlyByWire · A32NX 稳定版");

    companion object {
        fun fromId(id: Int) = entries.firstOrNull { it.wireId == id } ?: INIBUILDS
    }
}
