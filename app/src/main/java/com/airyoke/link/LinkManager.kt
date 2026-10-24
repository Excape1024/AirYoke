package com.airyoke.link

import android.util.Log
import com.airyoke.core.ControlOutput
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.locks.LockSupport
import kotlin.concurrent.thread

/**
 * 有线链路的总调度：通道协商、定频发送、掉线重建。
 *
 * ## 两条通道怎么选
 *
 * 按计划**优先 USB 网络共享（UDP）**，不可用再回退 adb reverse（TCP）。
 * 先试 UDP 而不是先试 TCP，是因为 UDP 更适合 100Hz 的连续操纵流；
 * 代价是只有 TCP 可用时要多等一小会儿（[UDP_WINDOW_MS]）。
 *
 * ## 发送速率与传感器解耦
 *
 * 传感器大约 47Hz，但对外固定按 [SEND_HZ] 发送。用独立线程定频，而不是
 * "来一帧传感器数据就发一帧"—— 这样输出节奏稳定，不随传感器抖动而变。
 */
class LinkManager(
    private val onTelemetry: (Protocol.Telemetry) -> Unit,
    private val onStatus: (Status) -> Unit,
) {

    enum class Channel { NONE, USB_TETHER_UDP, ADB_REVERSE_TCP }

    data class Status(
        val channel: Channel = Channel.NONE,
        /** 链路是否已就绪（可以发数据）。 */
        val ready: Boolean = false,
        /** 对端地址，UDP 下是从 beacon 学到的 PC 地址。 */
        val remote: String? = null,
        /** 实际发送频率。 */
        val outHz: Float = 0f,
        val sentCount: Long = 0,
    )

    private val udp = UdpLink(onBridgeFound = { addr ->
        Log.i(TAG, "UDP 通道就绪，对端 $addr")
        peerAddress = addr
    }, onTelemetry = { acceptTelemetry(Channel.USB_TETHER_UDP, it) })

    private val tcp = TcpLink(onTelemetry = { acceptTelemetry(Channel.ADB_REVERSE_TCP, it) })
    private var lastTelemetrySeq: Int? = null
    private var lastTelemetryAt = 0L

    @Synchronized private fun acceptTelemetry(source: Channel, t: Protocol.Telemetry) {
        if (source != channel) return
        val now = android.os.SystemClock.elapsedRealtime()
        val previous = lastTelemetrySeq
        // 以有符号差比较，支持序号回绕；重连超过超时后接受桥接端的新序列。
        if (previous != null && now - lastTelemetryAt < LINK_TIMEOUT_MS && t.seq - previous <= 0) return
        lastTelemetrySeq = t.seq
        lastTelemetryAt = now
        onTelemetry(t)
    }

    @Volatile
    private var running = false

    /**
     * 运行代次。每次 [stop] 都自增，让所有在跑的循环失效。
     *
     * 不能只靠 [running] 这个布尔量：`stop()` 之后旧线程可能正卡在 sleep 里，
     * 此时若 `start()` 立刻把 running 置回 true，旧线程醒来会以为一切正常，
     * 于是**新旧两套 supervisor + sender 同时运行** —— 发送频率翻倍、两个协商器互相打架。
     * 切出去看通知再切回来就会触发。
     */
    private val generation = AtomicInteger(0)

    @Volatile
    private var channel: Channel = Channel.NONE

    @Volatile
    private var peerAddress: String? = null

    /** 最近提交的一帧输出。发送线程只读它，所以用 volatile 就够了。 */
    @Volatile
    private var latest = Submission(ControlOutput(0f, 0f, 0f, 0f), false, false, AuxControls())

    private data class Submission(val output: ControlOutput, val calibrated: Boolean,
        val throttleEngaged: Boolean, val controls: AuxControls, val aircraft: Int = 1)

    /** 按键位掩码。离散事件，由界面按下时置位、发送后清除。 */
    private val pendingButtons = AtomicInteger(0)

    private var supervisorThread: Thread? = null
    private var senderThread: Thread? = null

    @Volatile
    private var sentCount = 0L

    @Volatile
    private var measuredHz = 0f

    fun start() {
        if (running) return
        running = true
        val gen = generation.incrementAndGet()
        supervisorThread = thread(name = "link-supervisor") { supervisorLoop(gen) }
        senderThread = thread(name = "link-sender") { senderLoop(gen) }
    }

    fun stop() {
        running = false
        // 代次自增：即使在跑的线程下一次检查时 running 又被 start() 置回 true，
        // 它们也会因为代次对不上而退出
        generation.incrementAndGet()
        udp.stop()
        tcp.close()
        channel = Channel.NONE
        supervisorThread = null
        senderThread = null
    }

    /** 这一代循环是否还应继续。 */
    private fun isLive(gen: Int): Boolean = running && generation.get() == gen

    /** 由传感器回调调用。只更新最新值，不做任何 I/O。 */
    fun submit(output: ControlOutput, calibrated: Boolean, throttleEngaged: Boolean,
        controls: AuxControls = AuxControls(), aircraft: Int = 1) {
        latest = Submission(output, calibrated, throttleEngaged, controls, aircraft)
    }

    /** 按下一个按键（起落架/襟翼等）。离散事件，会随下一帧发出后自动清除。 */
    fun pressButton(mask: Int) {
        pendingButtons.getAndUpdate { it or mask }
    }

    fun releaseButton(mask: Int) {
        pendingButtons.getAndUpdate { it and mask.inv() }
    }

    // ---------------- 通道协商 ----------------

    private fun supervisorLoop(gen: Int) {
        var nextStatusAt = 0L

        while (isLive(gen)) {
            if (channel == Channel.NONE) {
                channel = negotiate(gen)
                if (channel == Channel.ADB_REVERSE_TCP) {
                    peerAddress = "127.0.0.1:${Protocol.PORT}（adb reverse）"
                }
            } else if (isActiveLinkDead()) {
                Log.w(TAG, "链路 $channel 失联，重新协商")
                if (channel == Channel.ADB_REVERSE_TCP) tcp.close()
                channel = Channel.NONE
            }

            val now = System.currentTimeMillis()
            if (now >= nextStatusAt) {
                nextStatusAt = now + STATUS_INTERVAL_MS
                onStatus(
                    Status(
                        channel = channel,
                        ready = channel != Channel.NONE,
                        remote = peerAddress,
                        outHz = measuredHz,
                        sentCount = sentCount,
                    ),
                )
            }
            Thread.sleep(SUPERVISOR_TICK_MS)
        }
    }

    /** 先给 UDP 一个窗口，不行再连 TCP。返回协商结果。 */
    private fun negotiate(gen: Int): Channel {
        udp.start() // 幂等：内部已在跑就什么都不做

        val deadline = System.currentTimeMillis() + UDP_WINDOW_MS
        while (isLive(gen) && System.currentTimeMillis() < deadline) {
            if (udp.isReady) {
                Log.i(TAG, "选用通道 A：USB 网络共享 + UDP")
                return Channel.USB_TETHER_UDP
            }
            Thread.sleep(50)
        }

        if (!isLive(gen)) return Channel.NONE

        if (tcp.connect(TCP_CONNECT_TIMEOUT_MS)) {
            Log.i(TAG, "选用通道 B：adb reverse + TCP")
            return Channel.ADB_REVERSE_TCP
        }

        return Channel.NONE
    }

    /**
     * 判定活动链路是否已经死掉。
     *
     * 桥接程序即使没有操纵输入也会按 10Hz 回遥测、按 1Hz 发 beacon，
     * 所以"这么久没收到任何包"就等于断了。
     */
    private fun isActiveLinkDead(): Boolean {
        val last = when (channel) {
            Channel.USB_TETHER_UDP -> udp.lastReceiveMs
            Channel.ADB_REVERSE_TCP -> tcp.lastReceiveMs
            Channel.NONE -> return false
        }
        if (last == 0L) return false
        return System.currentTimeMillis() - last > LINK_TIMEOUT_MS
    }

    // ---------------- 定频发送 ----------------

    private fun senderLoop(gen: Int) {
        val packet = ByteArray(Protocol.STATE_SIZE)
        var seq = 0
        var nextSendNs = System.nanoTime()
        var windowStartMs = System.currentTimeMillis()
        var windowCount = 0

        while (isLive(gen)) {
            nextSendNs += SEND_INTERVAL_NS

            val active = channel
            if (active != Channel.NONE) {
                val snapshot = latest
                val output = snapshot.output
                val buttons = pendingButtons.get() or snapshot.controls.buttons

                var flags = (snapshot.aircraft and 3) shl 3
                if (snapshot.calibrated) flags = flags or Protocol.FLAG_CALIBRATED
                if (snapshot.throttleEngaged) flags = flags or Protocol.FLAG_THROTTLE_ENGAGED

                val state = Protocol.State(
                    seq = seq++,
                    flags = flags,
                    elevator = Protocol.axisToWire(output.elevator),
                    aileron = Protocol.axisToWire(output.aileron),
                    rudder = Protocol.axisToWire(output.rudder),
                    throttle = Protocol.axisToWire(output.throttle),
                    buttons = buttons,
                    aux = snapshot.controls.aux,
                    timestampMs = System.currentTimeMillis().toInt(),
                )
                Protocol.encodeState(state, packet)

                val sent = when (active) {
                    Channel.USB_TETHER_UDP -> udp.send(packet, Protocol.STATE_SIZE)
                    Channel.ADB_REVERSE_TCP -> tcp.send(packet, Protocol.STATE_SIZE)
                    Channel.NONE -> false
                }
                if (sent) {
                    sentCount++
                    windowCount++
                }
            }

            val nowMs = System.currentTimeMillis()
            if (nowMs - windowStartMs >= 1000) {
                measuredHz = windowCount * 1000f / (nowMs - windowStartMs)
                windowCount = 0
                windowStartMs = nowMs
            }

            val sleepNs = nextSendNs - System.nanoTime()
            if (sleepNs > 0) {
                LockSupport.parkNanos(sleepNs)
            } else {
                // 落后了就重新对齐，不要试图"补发"积压的帧——
                // 操纵数据只有最新一帧有意义，补发旧帧反而制造延迟。
                nextSendNs = System.nanoTime()
            }
        }
    }

    private companion object {
        const val TAG = "LinkManager"

        /** 发送频率。 */
        const val SEND_HZ = 100
        const val SEND_INTERVAL_NS = 1_000_000_000L / SEND_HZ

        /** 协商时留给 UDP 的发现窗口。 */
        const val UDP_WINDOW_MS = 1200L

        /** TCP 连接超时。本机回环，正常应瞬间完成。 */
        const val TCP_CONNECT_TIMEOUT_MS = 400

        /** 多久收不到任何包就判定链路已断。 */
        const val LINK_TIMEOUT_MS = 1500L

        const val SUPERVISOR_TICK_MS = 100L
        const val STATUS_INTERVAL_MS = 250L
    }
}
