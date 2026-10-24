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
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.airyoke.core.Attitude
import com.airyoke.core.AutopilotDisconnectRequest
import com.airyoke.core.ControlPipeline
import com.airyoke.core.ThrottleState
import com.airyoke.core.AircraftType
import com.airyoke.core.EngineFeedback
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
    private val throttleState2 = ThrottleState()
    private var aircraft = AircraftType.INIBUILDS
    private val autopilotDisconnect = AutopilotDisconnectRequest()
    private var rudder = 0f
    private var takeover = false
    private var yoke = true
    private var brake = false
    private var clUntil = 0L
    private val cl = 25f / 45f
    private val flex = 35f / 45f
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
        aircraft = AircraftType.fromId(prefs.getInt("aircraft.v5", 1))
        sensor = AttitudeProvider(this) { m -> m.copyInto(matrix); hasMatrix = true }
        link = LinkManager(onTelemetry = { packet ->
            val arrival = now()
            handler.post { telemetry = packet; receivedAt = arrival }
        }, onStatus = { status = it })
        setup()
    }
    private fun now() = SystemClock.elapsedRealtime()
    private fun live(): Boolean = now() - receivedAt < 1500 && telemetry?.systemFlags?.and(1) == 1
    private fun toast(text: String) { Toast.makeText(this, text, Toast.LENGTH_SHORT).show() }
    private fun controlReady(): Boolean =
        EngineFeedback.leversValid(telemetry, aircraft.wireId, now() - receivedAt) &&
            EngineFeedback.fresh(telemetry, aircraft.wireId, now() - receivedAt, Protocol.ENGINE_CONTROL_READY)

    private fun stopThrottle() {
        takeover = false; clUntil = 0
        sync = true; binding.throttleTakeover.isChecked = false; sync = false
        binding.throttleSlider.release()
        updateTakeover()
    }

    private fun selectAircraft(selected: AircraftType) {
        if (selected == aircraft) return
        stopThrottle()
        aircraft = selected
        telemetry = null; receivedAt = 0
        throttleState.reset(); throttleState2.reset()
        binding.throttleSlider.setValueSilently(0f)
        prefs.edit().putInt("aircraft.v5", aircraft.wireId).apply()
        configureDetents(); publish(); render()
    }

    private fun chooseAircraft() {
        AlertDialog.Builder(this).setTitle("选择正在飞行的机模")
            .setSingleChoiceItems(AircraftType.entries.map { it.title }.toTypedArray(), aircraft.ordinal) { dialog, index ->
                selectAircraft(AircraftType.entries[index]); dialog.dismiss()
            }.setNegativeButton("取消", null).show()
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun setup() {
        binding.throttleSlider.apply {
            label = "杆位①"; reverseDetentFraction = .18f; releaseNegativeToZero = true
            feedbackMode = true
            onValueChanged = {
                val reverseReady = EngineFeedback.fresh(telemetry, aircraft.wireId, now() - receivedAt, Protocol.ENGINE_REVERSE_READY)
                val target = if (it < 0 && !reverseReady) 0f else it
                if (target != it) {
                    setValueSilently(target)
                    toast("反推未就绪：请在飞机 EFB 启用并校准双轴反推")
                }
                throttleState.request(target); publish()
            }
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
                val t = telemetry
                if (!controlReady() || t == null || abs(t.throttlePos - t.throttlePos2) > .006f || !throttleState.takeOver()) {
                    stopThrottle()
                    toast("等待有效双杆回读；两杆需对齐后接管")
                    return@setOnCheckedChangeListener
                }
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
        binding.aircraftSelector.setOnClickListener { chooseAircraft() }
    }
    private fun configureDetents() {
        binding.throttleSlider.stops = listOf(-1f to "反推", 0f to "慢车", cl to "CL", flex to "FLX", 1f to "TOGA")
        binding.throttleSlider.travelMap = listOf(-1f to 0f, 0f to .18f, cl to .68f, flex to .86f, 1f to 1f)
        val strength = prefs.getInt("snap", 1).coerceIn(0, 2)
        binding.throttleSlider.snapper.capture = listOf(.02f, .03f, .04f)[strength]
        binding.throttleSlider.snapper.release = listOf(.035f, .05f, .065f)[strength]
        binding.throttleSlider.invalidate()
        binding.aircraftSelector.text = "${aircraft.title} ▾"
    }
    private fun updateTakeover() {
        binding.throttleSlider.isEnabled = takeover && controlReady()
        binding.throttleSlider.alpha = if (takeover) 1f else .55f
    }
    private fun publish() {
        if (!::link.isInitialized) return
        if ((takeover || clUntil > 0) && !controlReady()) stopThrottle()
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
                autopilotDisconnect = apAvailable && autopilotDisconnect.shouldSend(t)), aircraft.wireId)
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
        val elapsed = now() - receivedAt
        val leverValid = EngineFeedback.leversValid(t, aircraft.wireId, elapsed)
        val n1Valid = EngineFeedback.n1Valid(t, aircraft.wireId, elapsed)
        val mismatch = t != null && (t.aircraft != aircraft.wireId || t.engineFlags and Protocol.ENGINE_MODEL_MISMATCH != 0)
        if (mismatch) { throttleState.reset(); throttleState2.reset() }
        throttleState.receive(t?.throttlePos, leverValid)
        throttleState2.receive(t?.throttlePos2, leverValid)
        binding.throttleSlider.apply {
            feedbackValue = throttleState.actual
            feedbackValue2 = throttleState2.actual
            feedbackFresh = throttleState.feedbackFresh
            showTarget = takeover || now() < clUntil
        }
        binding.throttleTakeover.isEnabled = controlReady()
        updateTakeover()
        val engineStatus = when {
            mismatch -> "机模不匹配或尚未识别，请核对选择"
            !leverValid -> "杆位反馈不可用或已过期"
            !n1Valid -> "N1 反馈不可用或已过期"
            t!!.engineFlags and Protocol.ENGINE_REVERSE_READY == 0 -> "正推可用 · 反推需在 EFB 启用并校准"
            else -> "双杆反馈 · 样本年龄 ${t.engineAgeMs + elapsed} ms"
        }
        binding.engineText.text = if (n1Valid)
            "N1① ${String.format(java.util.Locale.ROOT, "%.1f", t!!.n1Left)}%   N1② ${String.format(java.util.Locale.ROOT, "%.1f", t.n1Right)}%\n$engineStatus"
            else "N1① —   N1② —\n$engineStatus"
        binding.statusText.text = when {
            valid -> if (pipeline.calibration.isCalibrated) "● 已连接  ·  ${status.outHz.roundToInt()} Hz" else "● 已连接  ·  请校准"
            status.ready -> "电脑已连 · 等待模拟器"
            else -> "等待电脑桥接 1.5.1（需两端配套）"
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
        val model = android.widget.Spinner(this).apply {
            adapter = android.widget.ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item,
                AircraftType.entries.map { it.title })
            setSelection(aircraft.ordinal)
        }
        val info = TextView(this).apply { text = "杆位：慢车 0° / CL 25° / FLX 35° / TOGA 45°\n油门轴按飞机 EFB 的双轴校准映射；N1 独立回读。" }
        val strength = android.widget.Spinner(this).apply {
            adapter = android.widget.ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item,
                arrayOf("吸附：轻柔", "吸附：标准", "吸附：较强"))
            setSelection(prefs.getInt("snap", 1).coerceIn(0, 2))
        }
        listOf(model, a, e, info, strength).forEach(panel::addView)
        val dialog = AlertDialog.Builder(this).setTitle("控制设置").setView(panel)
            .setNegativeButton("取消", null).setPositiveButton("保存", null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                selectAircraft(AircraftType.entries[model.selectedItemPosition])
                prefs.edit().putInt("snap", strength.selectedItemPosition).apply()
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
