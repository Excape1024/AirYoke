package com.airyoke.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import com.airyoke.core.DetentSnapper
import com.airyoke.core.LeverMapping
import kotlin.math.abs
import kotlin.math.roundToInt

/** 可同时触摸的长行程推杆，反推与脚舵在释放或取消时立即复位。 */
class AxisSliderView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null,
    defStyleAttr: Int = 0) : View(context, attrs, defStyleAttr) {
    enum class Orientation { VERTICAL, HORIZONTAL }
    var orientation = Orientation.VERTICAL
    var bipolar = false
    var reverseDetentFraction = 0f
    var autoCenter = false
    var showScale = false
    var reversed = false
    var releaseNegativeToZero = false
    var discrete = false
    var label = ""
    var stops: List<Pair<Float, String>> = emptyList()
    var travelMap: List<Pair<Float, Float>> = emptyList()
    val snapper = DetentSnapper()
    var value = 0f
        set(v) { field = if (v.isFinite()) v.coerceIn(-1f, 1f) else 0f; invalidate() }
    /** 仅油门启用：value 保留触控目标，主滑块单独显示模拟器反馈。 */
    var feedbackMode = false
        set(v) { field = v; invalidate() }
    var feedbackValue: Float? = null
        set(v) { field = v?.takeIf { it.isFinite() }?.coerceIn(-1f, 1f); invalidate() }
    var feedbackFresh = false
        set(v) { field = v; invalidate() }
    var showTarget = false
        set(v) { field = v; invalidate() }
    var onValueChanged: ((Float) -> Unit)? = null
    var onReleased: (() -> Unit)? = null
    var isEngaged = false
        private set
    private var pointerId = -1
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val rect = RectF()
    private val density = resources.displayMetrics.density
    private fun dp(v: Float) = v * density
    private val start get() = if (orientation == Orientation.VERTICAL) dp(if (feedbackMode) 62f else 46f) else dp(24f)
    private val end get() = if (orientation == Orientation.VERTICAL) height - dp(20f) else width - dp(24f)
    private val zero get() = if (bipolar) 0.5f else reverseDetentFraction.coerceIn(0f, .45f)
    private fun fraction(v: Float): Float {
        val f = if (travelMap.isNotEmpty()) LeverMapping.interpolate(v, travelMap)
            else if (v >= 0) zero + v * (1 - zero) else zero + v * zero
        return if (reversed) 1 - f else f
    }
    private fun fromFraction(f: Float): Float {
        val p = if (reversed) 1 - f else f
        if (travelMap.isNotEmpty()) return LeverMapping.interpolate(p, travelMap.map { it.second to it.first })
        return if (p >= zero) (p - zero) / (1 - zero) else if (zero > 0) (p - zero) / zero else 0f
    }
    fun setValueSilently(v: Float) { value = v }
    fun release() {
        if (autoCenter || (releaseNegativeToZero && value < 0)) {
            value = 0f
            onValueChanged?.invoke(value)
        }
        isEngaged = false
        pointerId = -1
        snapper.reset()
        onReleased?.invoke()
        invalidate()
    }
    override fun onDraw(canvas: Canvas) {
        val displayed = if (feedbackMode) feedbackValue else value
        val vertical = orientation == Orientation.VERTICAL
        val mid = if (vertical) width * .34f else height * .65f
        val rail = dp(if (vertical) 15f else 12f)
        paint.color = Color.rgb(35, 47, 60)
        if (vertical) rect.set(mid - rail, start, mid + rail, end)
        else rect.set(start, mid - rail, end, mid + rail)
        canvas.drawRoundRect(rect, dp(9f), dp(9f), paint)
        val position = if (vertical) end - fraction(displayed ?: 0f) * (end - start) else start + fraction(displayed ?: 0f) * (end - start)
        paint.color = if ((displayed ?: 0f) < 0) 0xFFFFB74D.toInt() else 0xFF52CBBB.toInt()
        val baseline = end - fraction(0f) * (end - start)
        if (vertical) rect.set(mid - dp(3f), minOf(position, baseline), mid + dp(3f), maxOf(position, baseline))
        else rect.set(minOf(position, width / 2f), mid - dp(3f), maxOf(position, width / 2f), mid + dp(3f))
        if (displayed != null) canvas.drawRoundRect(rect, dp(3f), dp(3f), paint)
        if (vertical) {
            val labels = if (stops.isNotEmpty()) stops else if (showScale) listOf(0f to "收", .5f to "½", 1f to "全") else emptyList()
            paint.textSize = dp(12f)
            paint.textAlign = Paint.Align.LEFT
            for ((v, text) in labels) {
                val y = end - fraction(v) * (end - start)
                paint.color = if (displayed != null && (!feedbackMode || feedbackFresh) && abs(displayed - v) < .006f) 0xFF66E0CE.toInt() else 0xFF9BACBD.toInt()
                paint.strokeWidth = dp(1.5f)
                canvas.drawLine(mid + rail, y, mid + rail + dp(7f), y, paint)
                canvas.drawText(text, mid + rail + dp(10f), y + dp(4f), paint)
            }
        }
        paint.color = if (feedbackMode && !feedbackFresh) 0xFF748696.toInt()
            else if (isEngaged) 0xFFABFFF0.toInt() else 0xFFDAE3EB.toInt()
        if (vertical) rect.set(mid - dp(25f), position - dp(11f), mid + dp(25f), position + dp(11f))
        else rect.set(position - dp(12f), mid - dp(13f), position + dp(12f), mid + dp(13f))
        if (displayed != null) canvas.drawRoundRect(rect, dp(5f), dp(5f), paint)
        paint.color = 0xFF748696.toInt()
        paint.strokeWidth = dp(2f)
        if (displayed != null) {
            if (vertical) canvas.drawLine(mid - dp(17f), position, mid + dp(17f), position, paint)
            else canvas.drawLine(position, mid - dp(8f), position, mid + dp(8f), paint)
        }
        // 目标使用橙色细线，回读更新不能移动触控目标或改变反推松手行为。
        if (feedbackMode && showTarget && (displayed == null || abs(value - displayed) >= .006f)) {
            val targetPosition = if (vertical) end - fraction(value) * (end - start) else start + fraction(value) * (end - start)
            paint.color = 0xFFFFB74D.toInt()
            paint.strokeWidth = dp(3f)
            if (vertical) canvas.drawLine(mid - dp(27f), targetPosition, mid + dp(27f), targetPosition, paint)
            else canvas.drawLine(targetPosition, mid - dp(15f), targetPosition, mid + dp(15f), paint)
        }
        paint.textAlign = Paint.Align.CENTER
        paint.textSize = dp(13f)
        paint.color = 0xFFD7E3EC.toInt()
        val caption = displayed?.let { displayCaption(it) } ?: "—"
        if (vertical) canvas.drawText("$label · $caption", width / 2f, dp(20f), paint)
        else canvas.drawText(label, width / 2f, dp(14f), paint)
        val feedbackStatus = if (feedbackMode) when {
            displayed == null -> "等待位置反馈"
            !feedbackFresh -> "位置反馈已过期"
            showTarget && value < 0 -> "反推位置暂不可用"
            showTarget -> "目标 ${displayCaption(value)}"
            else -> "飞机位置"
        } else ""
        if (feedbackMode && vertical) {
            paint.textSize = dp(10f)
            paint.color = if (!feedbackFresh || showTarget) 0xFFFFB74D.toInt() else 0xFF9BACBD.toInt()
            canvas.drawText(feedbackStatus, width / 2f, dp(36f), paint)
        }
        contentDescription = "$label $caption $feedbackStatus".trim()
    }
    private fun displayCaption(v: Float): String = stops.firstOrNull { abs(v - it.first) < .006f }?.second
        ?: if (v < 0 && !bipolar) "反推" else "${(v * 100).roundToInt()}%"
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (!isEnabled) return false
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                pointerId = event.getPointerId(0)
                isEngaged = true
                snapper.reset()
                parent?.requestDisallowInterceptTouchEvent(true)
                update(event, 0)
            }
            MotionEvent.ACTION_MOVE -> {
                val index = event.findPointerIndex(pointerId)
                if (index >= 0) update(event, index)
            }
            MotionEvent.ACTION_POINTER_UP -> if (event.getPointerId(event.actionIndex) == pointerId) release()
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                release()
                parent?.requestDisallowInterceptTouchEvent(false)
                if (event.actionMasked == MotionEvent.ACTION_UP) performClick()
            }
        }
        return true
    }
    private fun update(event: MotionEvent, index: Int) {
        if (!isEngaged || end <= start) return
        val raw = (if (orientation == Orientation.VERTICAL) (end - event.getY(index)) / (end - start)
            else (event.getX(index) - start) / (end - start)).coerceIn(0f, 1f)
        val previous = value
        val points = stops.map { fraction(it.first) }
        val snapped = if (discrete) points.minByOrNull { abs(it - raw) } ?: raw else snapper.apply(raw, points)
        value = fromFraction(snapped)
        if (stops.any { abs(value - it.first) < .001f } && abs(previous - value) > .001f)
            performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
        onValueChanged?.invoke(value)
    }
    override fun performClick(): Boolean { super.performClick(); return true }
    override fun onDetachedFromWindow() { release(); super.onDetachedFromWindow() }
}
