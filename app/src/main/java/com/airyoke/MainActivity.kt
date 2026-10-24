package com.airyoke

import android.annotation.SuppressLint
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.airyoke.core.Attitude
import com.airyoke.core.AutopilotDisconnectRequest
import com.airyoke.core.ControlPipeline
import com.airyoke.core.ThrottleState
import com.airyoke.databinding.ActivityMainBinding
import com.airyoke.link.AuxControls
import com.airyoke.link.LinkManager
import com.airyoke.link.Protocol
import com.airyoke.model.CalibrationStore
import com.airyoke.sensor.AttitudeProvider
import com.airyoke.ui.AxisSliderView
import kotlin.math.abs
import kotlin.math.roundToInt

/** 所有操作状态在主线程更新，网络线程读取不可变快照。 */
class MainActivity : AppCompatActivity() {
    private lateinit var binding: ActivityMainBinding
    private lateinit var store: CalibrationStore
    private lateinit var sensor: AttitudeProvider
    private lateinit var link: LinkManager
    private val pipeline = ControlPipeline()
    private val handler = Handler(Looper.getMainLooper())
    private val matrix = FloatArray(9)
    private var hasMatrix = false
    @Volatile private var status = LinkManager.Status()
    @Volatile private var telemetry: Protocol.Telemetry? = null
    @Volatile private var receivedAt = 0L
    private val throttleState = ThrottleState()
    private val autopilotDisconnect = AutopilotDisconnectRequest()
    private var rudder = 0f
    private var takeover = false
    private var yoke = true
    private var brake = false
    private var clUntil = 0L
    private var cl = .89f
    private var flex = .95f
    private var sync = false
    private var spoilers = 0f
    private var flaps = 0
    private var armed = false
    private var parking = false
    private var spoilersUntil = 0L
    private var flapsUntil = 0L
    private var armUntil = 0L
    private var parkingUntil = 0L
    private var pendingSpoilers = false
    private var pendingFlaps = false
    private var pendingArm = false
    private var pendingParking = false
    private val prefs by lazy { getSharedPreferences("airyoke.controls.v4", MODE_PRIVATE) }
    private val tick = object : Runnable {
        override fun run() { publish(); render(); handler.postDelayed(this, 20) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        store = CalibrationStore(this)
        pipeline.calibration.restore(store.loadCalibration())
        pipeline.aileronMapping = pipeline.aileronMapping.copy(inverted = store.loadInvertAileron())
        pipeline.elevatorMapping = pipeline.elevatorMapping.copy(inverted = store.loadInvertElevator())
        yoke = store.loadYokeEngaged()
        // 使用新键，避免沿用旧版自动学习到的零推力。
        cl = prefs.getFloat("cl", .89f).takeIf { it in .1f.. .94f } ?: .89f
        flex = prefs.getFloat("flex", .95f).takeIf { it > cl && it < 1f } ?: .95f
        sensor = AttitudeProvider(this) { m -> m.copyInto(matrix); hasMatrix = true }
        link = LinkManager(onTelemetry = { telemetry = it; receivedAt = now() }, onStatus = { status = it })
        setup()
    }
    private fun now() = SystemClock.elapsedRealtime()
    private fun live(): Boolean = now() - receivedAt < 1500 && telemetry?.systemFlags?.and(1) == 1
    private fun toast(text: String) { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }

    @SuppressLint("ClickableViewAccessibility")
    private fun setup() {
        binding.throttleSlider.apply {
            label = "油门"; reverseDetentFraction = .18f; releaseNegativeToZero = true
            feedbackMode = true
            onValueChanged = { throttleState.request(it); publish() }
        }
        configureDetents()
        binding.rudderSlider.apply {
            orientation = AxisSliderView.Orientation.HORIZONTAL; bipolar = true; autoCenter = true
            label = "方向舵"; onValueChanged = { rudder = it; publish() }
        }
        binding.spoilerSlider.apply {
            label = "减速板"; reversed = true; showScale = true
            onValueChanged = {
                spoilers = it; spoilersUntil = now() + 800; pendingSpoilers = true; publish()
            }
        }
        binding.flapsSlider.apply {
            label = "襟翼"; reversed = true; discrete = true
            stops = listOf(0f to "0", .25f to "1", .5f to "2", .75f to "3", 1f to "全")
            onValueChanged = {
                flaps = (it * 4).roundToInt(); flapsUntil = now() + 800; pendingFlaps = true; publish()
            }
        }
        binding.yokeEngaged.isChecked = yoke
        binding.yokeEngaged.setOnCheckedChangeListener { _, checked ->
            yoke = checked; store.saveYokeEngaged(checked); publish()
        }
        binding.throttleTakeover.setOnCheckedChangeListener { _, checked ->
            if (sync) return@setOnCheckedChangeListener
            if (checked) {
                throttleState.takeOver()
                binding.throttleSlider.setValueSilently(throttleState.target)
                clUntil = 0
            } else {
                binding.throttleSlider.release()
                throttleState.request(cl)
                binding.throttleSlider.setValueSilently(cl)
                clUntil = now() + 800
                toast("已发送 CL 目标；自动油门由座舱控制")
            }
            takeover = checked
            updateTakeover()
            publish()
        }
        updateTakeover()
        binding.spoilerArm.setOnCheckedChangeListener { _, checked ->
            if (!sync) { armed = checked; armUntil = now() + 800; pendingArm = true; publish() }
        }
        binding.parkingBrake.setOnCheckedChangeListener { _, checked ->
            if (!sync) { parking = checked; parkingUntil = now() + 800; pendingParking = true; publish() }
        }
        binding.apDisconnectButton.setOnClickListener {
            val t = telemetry
            if (live() && t != null && t.systemFlags and Protocol.SYSTEM_AP_DISCONNECT_SUPPORTED != 0) {
                autopilotDisconnect.request(now(), t.seq)
                publish()
                render()
            } else toast("等待支持断开功能的桥接程序与飞机反馈")
        }
        binding.brakeButton.setOnTouchListener { v, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> { brake = true; v.isPressed = true; publish() }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    brake = false; v.isPressed = false; publish()
                    if (event.actionMasked == MotionEvent.ACTION_UP) v.performClick()
                }
            }
            true
        }
        binding.calibrateButton.setOnClickListener {
            if (hasMatrix) { pipeline.zeroHere(matrix); store.saveCalibration(pipeline.calibration.save()); toast("姿态已校准") }
            else toast("等待姿态传感器")
        }
        binding.settingsButton.setOnClickListener { settings() }
    }
    private fun configureDetents() {
        binding.throttleSlider.stops = listOf(-1f to "反推", 0f to "慢车", cl to "CL", flex to "FLX", 1f to "TOGA")
        binding.throttleSlider.travelMap = listOf(-1f to 0f, 0f to .18f, cl to .68f, flex to .86f, 1f to 1f)
        val strength = prefs.getInt("snap", 1).coerceIn(0, 2)
        binding.throttleSlider.snapper.capture = listOf(.02f, .03f, .04f)[strength]
        binding.throttleSlider.snapper.release = listOf(.035f, .05f, .065f)[strength]
        binding.throttleSlider.invalidate()
    }
    private fun updateTakeover() {
        binding.throttleSlider.isEnabled = takeover
        binding.throttleSlider.alpha = if (takeover) 1f else .55f
    }
    private fun publish() {
        if (!::link.isInitialized) return
        val t = now()
        val feedback = telemetry
        val apAvailable = live() && feedback != null &&
            feedback.systemFlags and Protocol.SYSTEM_AP_DISCONNECT_SUPPORTED != 0
        autopilotDisconnect.update(t, apAvailable, feedback?.seq, feedback?.autopilotOn)
        val throttle = throttleState.target
        var output = pipeline.lastOutput.copy(rudder = rudder, throttle = throttle)
        if (hasMatrix) output = pipeline.update(matrix, rudder, throttle)
        if (!yoke) output = output.copy(aileron = 0f, elevator = 0f)
        val returning = !takeover && t < clUntil
        link.submit(output.copy(throttle = if (returning) cl else throttle),
            pipeline.calibration.isCalibrated, takeover || returning,
            AuxControls(brake, if (t < parkingUntil) parking else null,
                if (t < armUntil) armed else null,
                if (t < spoilersUntil) spoilers else null,
                if (t < flapsUntil) flaps else null,
                autopilotDisconnect = apAvailable && autopilotDisconnect.shouldSend(t)))
    }
    private fun render() {
        val axes = Attitude.toControlAxes(pipeline.lastEuler)
        binding.attitudeIndicator.calibrated = pipeline.calibration.isCalibrated
        binding.attitudeIndicator.rollDeg = -axes.aileronDeg
        binding.attitudeIndicator.pitchDeg = -axes.elevatorDeg
        val t = telemetry
        val valid = live() && t != null
        val apSupported = t != null && t.systemFlags and Protocol.SYSTEM_AP_DISCONNECT_SUPPORTED != 0
        binding.apDisconnectButton.isEnabled = valid && apSupported
        binding.apDisconnectButton.text = getString(when {
            !valid -> R.string.ap_disconnect_unavailable
            !apSupported -> R.string.ap_disconnect_upgrade
            autopilotDisconnect.status == AutopilotDisconnectRequest.Status.WAITING -> R.string.ap_disconnect_waiting
            t?.autopilotOn == false -> R.string.ap_disconnect_off
            autopilotDisconnect.status == AutopilotDisconnectRequest.Status.UNCONFIRMED -> R.string.ap_disconnect_unconfirmed
            else -> R.string.ap_disconnect
        })
        throttleState.receive(t?.throttlePos, valid)
        binding.throttleSlider.apply {
            feedbackValue = throttleState.actual
            feedbackFresh = throttleState.feedbackFresh
            showTarget = takeover || now() < clUntil
        }
        binding.statusText.text = when {
            valid -> if (pipeline.calibration.isCalibrated) "● 已连接  ·  ${status.outHz.roundToInt()} Hz" else "● 已连接  ·  请校准"
            status.ready -> "电脑已连 · 等待模拟器"
            else -> "等待电脑桥接 1.5"
        }
        if (valid) {
            binding.flightText.text = "${t.airspeedKt.roundToInt()} kt   ·   ${t.altitudeFt.roundToInt()} ft"
            if (abs(t.spoilersPos - spoilers) < .035f) pendingSpoilers = false
            if (t.flapsIndex == flaps) pendingFlaps = false
            if ((t.systemFlags and 2 != 0) == armed) pendingArm = false
            if ((t.systemFlags and 4 != 0) == parking) pendingParking = false
            if (!binding.spoilerSlider.isEngaged && now() >= spoilersUntil && !pendingSpoilers)
                binding.spoilerSlider.setValueSilently(t.spoilersPos)
            if (!binding.flapsSlider.isEngaged && now() >= flapsUntil && !pendingFlaps)
                binding.flapsSlider.setValueSilently(t.flapsIndex.coerceIn(0, 4) / 4f)
            sync = true
            if (now() >= armUntil && !pendingArm) binding.spoilerArm.isChecked = t.systemFlags and 2 != 0
            if (now() >= parkingUntil && !pendingParking) binding.parkingBrake.isChecked = t.systemFlags and 4 != 0
            sync = false
            val pending = pendingSpoilers || pendingFlaps || pendingArm || pendingParking
            binding.feedbackText.text = if (pending) "指令未确认 · 襟翼 ${t.flapsIndex} / 板 ${(t.spoilersPos * 100).roundToInt()}%"
                else "${if (t.autopilotOn) "AP  " else ""}${if (t.autothrottleOn) "A/THR  " else ""}襟翼 ${t.flapsIndex} · 板 ${(t.spoilersPos * 100).roundToInt()}%"
        } else {
            binding.flightText.text = "— kt   ·   — ft"
            binding.feedbackText.text = "${if (brake) "刹车按下" else ""}  无有效模拟器回读"
        }
    }
    private fun settings() {
        val panel = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(32, 8, 32, 8) }
        val a = CheckBox(this).apply { text = "副翼反转"; isChecked = store.loadInvertAileron() }
        val e = CheckBox(this).apply { text = "升降舵反转"; isChecked = store.loadInvertElevator() }
        val info = TextView(this).apply { text = "A320 档位轴值（%）\n默认机型 CL 89、FLX 95。更换机型时按座舱实际卡位调整。" }
        val c = EditText(this).apply { hint = "CL"; setText((cl * 100).toString()); inputType = 8194 }
        val f = EditText(this).apply { hint = "FLX"; setText((flex * 100).toString()); inputType = 8194 }
        val strength = android.widget.Spinner(this).apply {
            adapter = android.widget.ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item,
                arrayOf("吸附：轻柔", "吸附：标准", "吸附：较强"))
            setSelection(prefs.getInt("snap", 1).coerceIn(0, 2))
        }
        listOf(a, e, info, c, f, strength).forEach(panel::addView)
        val dialog = AlertDialog.Builder(this).setTitle("控制设置").setView(panel)
            .setNegativeButton("取消", null).setPositiveButton("保存", null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val cv = c.text.toString().toFloatOrNull()?.div(100)
                val fv = f.text.toString().toFloatOrNull()?.div(100)
                if (cv == null || fv == null || cv !in .1f.. .94f || fv <= cv || fv >= 1) {
                    toast("要求 10 ≤ CL < FLX < 100，且 CL ≤ 94"); return@setOnClickListener
                }
                cl = cv; flex = fv
                prefs.edit().putFloat("cl", cl).putFloat("flex", flex).putInt("snap", strength.selectedItemPosition).apply()
                store.saveInvertAileron(a.isChecked); store.saveInvertElevator(e.isChecked)
                pipeline.aileronMapping = pipeline.aileronMapping.copy(inverted = a.isChecked)
                pipeline.elevatorMapping = pipeline.elevatorMapping.copy(inverted = e.isChecked)
                configureDetents(); dialog.dismiss()
            }
        }
        dialog.show()
    }
    override fun onResume() { super.onResume(); sensor.start(); link.start(); handler.post(tick) }
    override fun onPause() {
        handler.removeCallbacks(tick)
        binding.throttleSlider.release(); binding.rudderSlider.release()
        brake = false; clUntil = 0; takeover = false
        autopilotDisconnect.cancel()
        spoilersUntil = 0; flapsUntil = 0; armUntil = 0; parkingUntil = 0
        // 断线保护由桥接端看门狗兜底，不依赖最后一个包必达。
        publish(); sensor.stop(); link.stop()
        sync = true
        binding.throttleTakeover.isChecked = false
        sync = false
        updateTakeover()
        super.onPause()
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
        } else if (::binding.isInitialized) {
            binding.throttleSlider.release(); binding.rudderSlider.release(); brake = false; publish()
        }
    }
}
