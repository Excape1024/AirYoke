package com.airyoke.link

/** 协议四：按键高位是有效位，辅助字低十位为减速板，随后三位为襟翼档。 */
data class AuxControls(
    val brake: Boolean = false,
    val parking: Boolean? = null,
    val armed: Boolean? = null,
    val spoilers: Float? = null,
    val flaps: Int? = null,
) {
    val buttons: Int get() = (if (brake) 0x100 else 0) or
        (if (parking != null) 0x200 or (if (parking) 0x400 else 0) else 0) or
        (if (armed != null) 0x800 or (if (armed) 0x1000 else 0) else 0) or
        (if (spoilers != null) 0x2000 else 0) or (if (flaps != null) 0x4000 else 0)
    val aux: Int get() = (((spoilers ?: 0f).coerceIn(0f, 1f) * 1023).toInt()) or
        ((flaps ?: 0).coerceIn(0, 4) shl 10)
}
