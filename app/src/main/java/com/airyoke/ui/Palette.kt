package com.airyoke.ui

import android.graphics.Color

/**
 * 界面配色，全部预先解析成 int。
 *
 * 之所以要单独提出来：`Color.parseColor` 每次调用都要解析一遍字符串。
 * 之前这些颜色直接写在 `onDraw` 里，等于**每帧解析几十次颜色字符串** ——
 * 在 60fps 下就是每秒上千次无谓的解析和短命字符串分配。
 */
internal object Palette {

    /** 座舱底色。需与 `colors.xml` 里的 `cockpit_background` 保持一致。 */
    val Background = Color.parseColor("#0B141C")

    // 文字
    val Label = Color.parseColor("#CFD8DC")
    val Value = Color.WHITE
    val Muted = Color.parseColor("#78909C")
    val LabelText = Color.parseColor("#ECEFF1")
    val ScaleText = Color.parseColor("#90A4AE")

    // 轨道与刻度
    val Track = Color.parseColor("#1E2A33")
    val Center = Color.parseColor("#607D8B")
    val Tick = Color.parseColor("#546E7A")
    val Idle = Color.parseColor("#546E7A")

    // 语义色：越界/方向用冷暖区分，一眼能看出在哪一侧
    val Positive = Color.parseColor("#4FC3F7")
    val Negative = Color.parseColor("#FF8A65")
    val Throttle = Color.parseColor("#66BB6A")
    val Reverse = Color.parseColor("#FFB74D")
    val Detent = Color.parseColor("#FFB74D")

    // 人工地平仪
    val Sky = Color.parseColor("#1F6FA8")
    val Ground = Color.parseColor("#7A5326")
    val Bezel = Color.parseColor("#37474F")
    val Aircraft = Color.parseColor("#FFB74D")

    /** 未校准遮罩。 */
    val Scrim = Color.parseColor("#B3000000")
}
