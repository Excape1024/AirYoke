package com.airyoke.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.util.AttributeSet
import android.view.View
import kotlin.math.abs

/**
 * 单轴输出条：从中心向两侧填充，显示 −1..1 的归一化输出。
 *
 * 调参和校准时主要看这个 —— 校准后它应该稳定停在正中。
 * 右侧同时显示归一化值和原始量，方便判断到底是"死区没吃掉"还是"增益不对"。
 */
class AxisBarView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
) : View(context, attrs, defStyleAttr) {

    /** 轴名称。 */
    var label: String = ""
        set(value) {
            field = value
            invalidate()
        }

    /** 归一化输出 −1..1。 */
    var value: Float = 0f
        set(newValue) {
            if (field != newValue) {
                field = newValue
                invalidate()
            }
        }

    /**
     * 右侧的辅助读数，由调用方格式化好。
     *
     * 不在这里定单位，是因为四个轴量的"原始值"含义不同：
     * 副翼和升降舵是角度（`+12.3°`），方向舵是百分比，油门还可能是反推。
     */
    var rawText: String = ""
        set(newValue) {
            if (field != newValue) {
                field = newValue
                invalidate()
            }
        }

    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Palette.Label
        textAlign = Paint.Align.LEFT
    }

    private val valuePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Palette.Value
        textAlign = Paint.Align.RIGHT
        typeface = Typeface.MONOSPACE
    }

    private val trackPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Palette.Track }

    private val centerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Palette.Idle
        strokeWidth = 2f
    }

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG)

    private val trackRect = RectF()

    /** 复用同一个格式化器，避免每帧 new。 */
    private val valueBuffer = StringBuilder(8)

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)

        val h = height.toFloat()
        val w = width.toFloat()
        labelPaint.textSize = h * 0.30f
        valuePaint.textSize = h * 0.30f

        val labelWidth = w * 0.16f
        val valueWidth = w * 0.30f
        val trackLeft = labelWidth + h * 0.10f
        val trackRight = w - valueWidth - h * 0.10f
        val trackTop = h * 0.22f
        val trackBottom = h * 0.78f

        canvas.drawText(label, 0f, h * 0.68f, labelPaint)

        trackRect.set(trackLeft, trackTop, trackRight, trackBottom)
        val radius = (trackBottom - trackTop) / 2f
        canvas.drawRoundRect(trackRect, radius, radius, trackPaint)

        val centerX = (trackLeft + trackRight) / 2f
        canvas.drawLine(centerX, trackTop - h * 0.10f, centerX, trackBottom + h * 0.10f, centerPaint)

        val clamped = value.coerceIn(-1f, 1f)
        val halfSpan = (trackRight - trackLeft) / 2f

        fillPaint.color = when {
            abs(clamped) < 0.01f -> Palette.Idle
            clamped > 0 -> Palette.Positive
            else -> Palette.Negative
        }

        if (abs(clamped) > 0.001f) {
            val endX = centerX + halfSpan * clamped
            trackRect.set(minOf(centerX, endX), trackTop, maxOf(centerX, endX), trackBottom)
            canvas.drawRoundRect(trackRect, radius, radius, fillPaint)
        }

        // 数值：归一化输出 + 辅助读数
        valueBuffer.setLength(0)
        valueBuffer.append(if (clamped < 0f) '-' else '+')
        appendFixed2(valueBuffer, abs(clamped))
        canvas.drawText(valueBuffer, 0, valueBuffer.length, w, h * 0.68f, valuePaint)

        valuePaint.color = Palette.Muted
        canvas.drawText(rawText, w - valueWidth * 0.52f, h * 0.68f, valuePaint)
        valuePaint.color = Palette.Value
    }

    /** 手写两位小数格式化，避开 `String.format` 每次都要解析格式串的开销。 */
    private fun appendFixed2(sb: StringBuilder, v: Float) {
        val scaled = (v * 100f + 0.5f).toInt()
        sb.append(scaled / 100)
        sb.append('.')
        val frac = scaled % 100
        if (frac < 10) sb.append('0')
        sb.append(frac)
    }
}
