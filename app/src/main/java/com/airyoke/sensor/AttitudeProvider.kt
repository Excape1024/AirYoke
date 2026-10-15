package com.airyoke.sensor

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.SystemClock
import android.util.Log

/**
 * 采集手机姿态，输出旋转矩阵。
 *
 * ## 为什么用 [Sensor.TYPE_GAME_ROTATION_VECTOR] 而不是 [Sensor.TYPE_ROTATION_VECTOR]
 *
 * |                  | 融合来源        | 磁干扰 | 偏航漂移 | 俯仰/滚转 |
 * |------------------|-----------------|--------|----------|-----------|
 * | ROTATION_VECTOR  | 陀螺+加速度+磁力计 | 敏感   | 无       | 准        |
 * | GAME_ROTATION_VECTOR | 陀螺+加速度   | 免疫   | 有       | 一样准    |
 *
 * 本 App **只使用俯仰和滚转**（方向舵与油门走屏幕滑条），而这两个角由重力确定，
 * 与磁力计无关。于是游戏版严格更优：完全免疫室内磁干扰，而它唯一的缺点（偏航漂移）
 * 恰好是我们不用的那个量。
 *
 * ## 传感器速率
 *
 * 用 `SENSOR_DELAY_GAME`（约 50Hz）。手机上 `getRotationMatrixFromVector` 的结果会
 * 在内部做平滑，要更高的原始速率意义不大；100Hz 的输出由 [com.airyoke.core] 侧的
 * 发送节奏决定，与传感器回调速率解耦。
 *
 * 需要在清单里声明 `HIGH_SAMPLING_RATE_SENSORS`，否则 targetSdk 31+ 会被系统限速。
 */
class AttitudeProvider(
    context: Context,
    /** 收到新的旋转矩阵时回调。**注意：数组会被复用，回调内必须立即消费或自行复制。** */
    private val onRotationMatrix: (FloatArray) -> Unit,
) : SensorEventListener {

    private val sensorManager =
        context.getSystemService(Context.SENSOR_SERVICE) as SensorManager

    private val sensor: Sensor? =
        sensorManager.getDefaultSensor(Sensor.TYPE_GAME_ROTATION_VECTOR)
            ?: sensorManager.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)

    /** 实际选中的传感器类型，用于在界面上如实显示来源。 */
    val sensorType: Int? get() = sensor?.type

    val isAvailable: Boolean get() = sensor != null

    /** 复用的旋转矩阵缓冲，避免每帧分配。回调出去的就是它。 */
    private val rotationMatrix = FloatArray(9)

    // ---- 实际采样率统计：用来发现"麦克风隐私开关强制限速"这类问题 ----

    private var sampleCount = 0
    private var windowStartMs = 0L
    @Volatile
    var measuredHz: Float = 0f
        private set

    /** 是否已注册。重复注册会让同一份事件被投递多次。 */
    private var registered = false

    fun start() {
        if (registered) return

        val target = sensor ?: run {
            Log.w(TAG, "设备没有旋转矢量传感器，无法采集姿态")
            return
        }
        sensorManager.registerListener(this, target, SensorManager.SENSOR_DELAY_GAME)
        registered = true
        sampleCount = 0
        windowStartMs = SystemClock.elapsedRealtime()
    }

    fun stop() {
        if (!registered) return
        sensorManager.unregisterListener(this)
        registered = false
    }

    override fun onSensorChanged(event: SensorEvent) {
        SensorManager.getRotationMatrixFromVector(rotationMatrix, event.values)
        onRotationMatrix(rotationMatrix)
        updateRateStats()
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {
        // 精度变化不影响俯仰/滚转：这两个角靠重力定，不依赖磁力计。
    }

    private fun updateRateStats() {
        val now = SystemClock.elapsedRealtime()
        sampleCount++
        val elapsed = now - windowStartMs
        if (elapsed >= RATE_WINDOW_MS) {
            measuredHz = sampleCount * 1000f / elapsed
            sampleCount = 0
            windowStartMs = now
        }
    }

    private companion object {
        const val TAG = "AttitudeProvider"
        const val RATE_WINDOW_MS = 1000L
    }
}
