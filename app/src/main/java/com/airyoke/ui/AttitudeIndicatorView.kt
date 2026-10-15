package com.airyoke.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import kotlin.math.min

/**
 * 人工地平仪。
 *
 * 手持手机当驾驶盘时，这块仪表就是"我现在的姿态对不对"的唯一反馈 ——
 * 校准时的参照、以及飞行中判断有没有漂移，都靠它。所以它占据屏幕中央。
 *
 * 画法是标准的姿态仪结构：
 *  - 天/地两色按 [rollDeg] 旋转、按 [pitchDeg] 平移
 *  - 俯仰刻度梯每 10° 一条
 *  - 中央固定机翼符号（代表飞机自身，不动）
 *  - 顶部坡度刻度弧 + 随坡度转动的指针
 */
class AttitudeIndicatorView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
) : View(context, attrs, defStyleAttr) {

    /** 坡度（滚转角），单位度。正 = 右倾。 */
    var rollDeg: Float = 0f
        set(value) {
            if (field != value) {
                field = value
                invalidate()
            }
        }

    /** 俯仰角，单位度。正 = 抬头。 */
    var pitchDeg: Float = 0f
        set(value) {
            if (field != value) {
                field = value
                invalidate()
            }
        }

    /** 是否已校准。未校准时整体压暗，提示读数无意义。 */
    var calibrated: Boolean = false
        set(value) {
            if (field != value) {
                field = value
                invalidate()
            }
        }

    private val skyPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = SKY }
    private val groundPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = GROUND }
    private val horizonPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        strokeWidth = 3f
        style = Paint.Style.STROKE
    }

    private val ladderPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        strokeWidth = 2f
        style = Paint.Style.STROKE
    }

    private val ladderTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textAlign = Paint.Align.CENTER
    }

    private val bezelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = BEZEL
        style = Paint.Style.STROKE
        strokeWidth = 6f
    }

    private val markPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        strokeWidth = 3f
        style = Paint.Style.STROKE
    }

    private val aircraftPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = AIRCRAFT
        strokeWidth = 5f
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
    }

    private val pointerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = AIRCRAFT }
    private val scrimPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xB3000000.toInt() }
    private val overlayTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textAlign = Paint.Align.CENTER
    }

    // 绘制路径与矩形在 onDraw 里复用：这块视图每帧都重绘，
    // 在里面 new 对象会让 GC 在 60fps 下持续抖动。
    private val clipPath = Path()
    private val oval = RectF()
    private val pointerPath = Path()

    /**
     * 俯仰刻度数字。预先算好，因为这段绘制每帧都跑 ——
     * 在这里 `toString()` 会以每秒上千次的频率制造短命字符串，白白喂给 GC。
     */
    private val pitchLabels = Array(10) { (it * 10).toString() }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)

        val cx = width / 2f
        val cy = height / 2f
        val radius = min(cx, cy) - BEZEL_WIDTH

        // 圆形裁剪：所有天地/刻度都画在这个圆里
        clipPath.reset()
        clipPath.addCircle(cx, cy, radius, Path.Direction.CW)
        canvas.save()
        canvas.clipPath(clipPath)

        canvas.rotate(-rollDeg, cx, cy)
        val pixelsPerDegree = radius / VISIBLE_PITCH_DEG
        canvas.translate(0f, pitchDeg * pixelsPerDegree)

        // 天地两色。画得足够大，转多少度都不会露出空白。
        val extent = radius * 4f
        canvas.drawRect(cx - extent, cy - extent, cx + extent, cy, skyPaint)
        canvas.drawRect(cx - extent, cy, cx + extent, cy + extent, groundPaint)

        // 地平线
        canvas.drawLine(cx - extent, cy, cx + extent, cy, horizonPaint)

        drawPitchLadder(canvas, cx, cy, radius, pixelsPerDegree)

        canvas.restore()

        drawBezel(canvas, cx, cy, radius)
        drawAircraftSymbol(canvas, cx, cy, radius)

        if (!calibrated) drawUncalibratedOverlay(canvas, cx, cy, radius)
    }

    /** 俯仰刻度梯：每 10° 一条，正负两侧对称，每 10° 标注数字。 */
    private fun drawPitchLadder(
        canvas: Canvas,
        cx: Float,
        cy: Float,
        radius: Float,
        pixelsPerDegree: Float,
    ) {
        ladderTextPaint.textSize = radius * 0.10f
        val halfWidth = radius * 0.34f
        val gap = radius * 0.13f

        var deg = -90
        while (deg <= 90) {
            if (deg == 0) {
                deg += 10
                continue
            }
            val y = cy - deg * pixelsPerDegree
            if (y < cy - radius * 1.6f || y > cy + radius * 1.6f) {
                deg += 10
                continue
            }

            // deg 是 ±10..±90 的整数，索引到预算好的标签表
            val labelIndex = kotlin.math.abs(deg) / 10

            if (deg > 0) {
                // 抬头刻度：中间断开的横线
                canvas.drawLine(cx - halfWidth, y, cx - gap, y, ladderPaint)
                canvas.drawLine(cx + gap, y, cx + halfWidth, y, ladderPaint)
            } else {
                // 低头刻度：画成虚线以示区分
                var x = cx - halfWidth
                while (x < cx - gap) {
                    canvas.drawLine(x, y, min(x + radius * 0.06f, cx - gap), y, ladderPaint)
                    x += radius * 0.10f
                }
                x = cx + gap
                while (x < cx + halfWidth) {
                    canvas.drawLine(x, y, min(x + radius * 0.06f, cx + halfWidth), y, ladderPaint)
                    x += radius * 0.10f
                }
            }

            canvas.drawText(
                pitchLabels[labelIndex],
                cx - halfWidth - radius * 0.10f,
                y + radius * 0.035f,
                ladderTextPaint,
            )
            canvas.drawText(
                pitchLabels[labelIndex],
                cx + halfWidth + radius * 0.10f,
                y + radius * 0.035f,
                ladderTextPaint,
            )

            deg += 10
        }
    }

    /** 外圈、坡度刻度弧与坡度指针。 */
    private fun drawBezel(canvas: Canvas, cx: Float, cy: Float, radius: Float) {
        canvas.drawCircle(cx, cy, radius, bezelPaint)

        // 坡度刻度弧（顶部 ±60°）
        oval.set(cx - radius, cy - radius, cx + radius, cy + radius)
        canvas.save()
        canvas.rotate(-60f, cx, cy)
        var tick = 0
        while (tick <= 12) {
            val isMajor = tick % 3 == 0
            val length = if (isMajor) radius * 0.10f else radius * 0.05f
            canvas.drawLine(cx, cy - radius, cx, cy - radius + length, markPaint)
            canvas.rotate(10f, cx, cy)
            tick++
        }
        canvas.restore()

        // 坡度指针：随坡度转动，指向刻度弧
        canvas.save()
        canvas.rotate(-rollDeg, cx, cy)
        pointerPath.reset()
        pointerPath.moveTo(cx, cy - radius + radius * 0.14f)
        pointerPath.lineTo(cx - radius * 0.05f, cy - radius + radius * 0.02f)
        pointerPath.lineTo(cx + radius * 0.05f, cy - radius + radius * 0.02f)
        pointerPath.close()
        canvas.drawPath(pointerPath, pointerPaint)
        canvas.restore()
    }

    /** 中央机翼符号 —— 代表飞机自身，永远不动。 */
    private fun drawAircraftSymbol(canvas: Canvas, cx: Float, cy: Float, radius: Float) {
        val wing = radius * 0.40f
        val inset = radius * 0.10f
        // 左右两段机翼，中间留空
        canvas.drawLine(cx - wing, cy, cx - inset, cy, aircraftPaint)
        canvas.drawLine(cx + inset, cy, cx + wing, cy, aircraftPaint)
        // 中央小点
        canvas.drawCircle(cx, cy, radius * 0.022f, aircraftPaint)
    }

    /** 未校准时压暗并提示。 */
    private fun drawUncalibratedOverlay(canvas: Canvas, cx: Float, cy: Float, radius: Float) {
        canvas.drawCircle(cx, cy, radius, scrimPaint)
        overlayTextPaint.textSize = radius * 0.16f
        canvas.drawText("未校准", cx, cy + radius * 0.05f, overlayTextPaint)
    }

    private companion object {
        const val SKY = 0xFF1F6FA8.toInt()
        const val GROUND = 0xFF7A5326.toInt()
        const val BEZEL = 0xFF37474F.toInt()
        const val AIRCRAFT = 0xFFFFB74D.toInt()
        const val BEZEL_WIDTH = 8f
        /** 圆内可见的俯仰范围（±这个度数）。 */
        const val VISIBLE_PITCH_DEG = 45f
    }
}
