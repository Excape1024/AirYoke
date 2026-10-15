package com.airyoke.link

import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import kotlin.concurrent.thread

/**
 * 通道 A：USB 网络共享 + UDP（首选）。
 *
 * 手机打开「USB 网络共享」后，Android 会建一条虚拟网线，PC 侧由手机的 DHCP 分配地址。
 * Windows 11 原生识别这种网卡（RNDIS），免装驱动。
 *
 * **网段不是固定的**：Android 12 起会随机挑一个私有网段（老版本才是 `192.168.42.0/24`）。
 * 所以这里一律实地枚举网卡，不假设任何网段 —— 详见 [broadcastTargets]。
 *
 * ## 怎么找到 PC（不需要用户填任何 IP）
 *
 * 手机周期性把 [Protocol.KIND_DISCOVER] 广播到子网广播地址，桥接程序收到后回
 * [Protocol.KIND_BEACON]；手机从**回包的源地址**得知 PC 在哪。桥接程序自己也会主动
 * 广播 beacon，两条路都通，谁先到用谁。
 *
 * 选 UDP 而不是 TCP 是因为这里是 100Hz 的连续操纵数据：UDP 无队头阻塞、丢包容忍，
 * 偶尔丢一帧只是少一个采样点，而 TCP 的补发重传反而会造成操纵延迟抖动。
 *
 * ## 关于重启时的并发
 *
 * 和 [TcpLink] 同理：线程退出时只能清理**它自己那条** socket。
 * 所有清理都先过 [isCurrent] 判断，避免旧线程误关新连接。
 */
class UdpLink(
    private val onBridgeFound: (String) -> Unit,
    private val onTelemetry: (Protocol.Telemetry) -> Unit,
) {

    private var socket: DatagramSocket? = null

    @Volatile
    private var bridgeAddress: InetAddress? = null

    /** 最近一次收到桥接程序任何包的时刻，用于判定链路是否还活着。 */
    @Volatile
    var lastReceiveMs: Long = 0L
        private set

    /** 已经找到桥接程序，可以发数据了。 */
    val isReady: Boolean get() = bridgeAddress != null && socket != null

    fun start() {
        if (socket != null) return
        try {
            // 绑到通配地址才能收到发往本机任一网卡的广播/单播
            val s = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(Protocol.PORT))
            }
            socket = s
            thread(name = "udp-rx") { receiveLoop(s) }
            thread(name = "udp-discover") { discoverLoop(s) }
            Log.i(TAG, "UDP 通道已监听 :${Protocol.PORT}")
        } catch (e: Exception) {
            Log.w(TAG, "UDP 通道启动失败（USB 网络共享可能没开）", e)
        }
    }

    fun stop() {
        val s = socket
        socket = null
        bridgeAddress = null
        try {
            s?.close()
        } catch (_: Exception) {
        }
    }

    /** 发一个状态包。未找到桥接程序时静默丢弃。 */
    fun send(bytes: ByteArray, length: Int): Boolean {
        val s = socket ?: return false
        val target = bridgeAddress ?: return false
        return try {
            s.send(DatagramPacket(bytes, length, target, Protocol.PORT))
            true
        } catch (e: Exception) {
            Log.w(TAG, "UDP 发送失败", e)
            false
        }
    }

    /** 这条 socket 是否仍是当前连接。 */
    private fun isCurrent(candidate: DatagramSocket): Boolean = socket === candidate

    private fun receiveLoop(own: DatagramSocket) {
        val buf = ByteArray(2048)
        while (isCurrent(own)) {
            try {
                val packet = DatagramPacket(buf, buf.size)
                own.receive(packet)
                if (!isCurrent(own)) break

                lastReceiveMs = System.currentTimeMillis()
                handlePacket(packet, buf)
            } catch (e: Exception) {
                if (isCurrent(own)) Log.w(TAG, "UDP 接收异常", e)
                return
            }
        }
    }

    private fun handlePacket(packet: DatagramPacket, buf: ByteArray) {
        val len = packet.length

        when (Protocol.decodeHandshake(buf, len)) {
            Protocol.KIND_BEACON -> {
                val from = packet.address ?: return
                if (bridgeAddress != from) {
                    bridgeAddress = from
                    Log.i(TAG, "发现桥接程序：$from")
                    onBridgeFound(from.hostAddress ?: "?")
                }
            }
            Protocol.KIND_DISCOVER -> Unit // 我们只发不收，忽略
            else -> Protocol.decodeTelemetry(buf, len)?.let(onTelemetry)
        }
    }

    /** 在拿到 beacon 之前，反复广播发现包。拿到之后这个线程就安静了。 */
    private fun discoverLoop(own: DatagramSocket) {
        val probe = ByteArray(Protocol.HANDSHAKE_SIZE)
        Protocol.encodeHandshake(Protocol.KIND_DISCOVER, probe)

        while (isCurrent(own)) {
            try {
                if (!isReady) {
                    for (target in broadcastTargets()) {
                        own.send(DatagramPacket(probe, probe.size, target, Protocol.PORT))
                    }
                }
                Thread.sleep(if (isReady) DISCOVER_IDLE_MS else DISCOVER_ACTIVE_MS)
            } catch (e: InterruptedException) {
                return
            } catch (e: Exception) {
                if (isCurrent(own)) Log.w(TAG, "UDP 发现广播失败", e)
                return
            }
        }
    }

    /**
     * 算出该往哪些地址发发现包。
     *
     * **绝不能按网段认网卡。** Android 12 起，USB 网络共享不再固定使用
     * `192.168.42.0/24`，而是每次开启时随机挑一个私有网段
     * （实测这台机器上是 `10.24.237.0/24`）。按 `192.168.42.` 匹配的后果是
     * 一个网卡都匹配不上 —— 发现包被发到根本不存在的广播地址上，
     * 手机永远找不到 PC，而链路本身其实完全正常。
     *
     * 所以改成按**接口名**认：USB 网络共享在 Android 上叫 `rndis0` / `usb0` / `ncm0`。
     * 认不出来就把所有已启用的非回环 IPv4 网卡都试一遍 ——
     * 宁可多发几个 6 字节的小包，也不要让用户连不上。
     */
    private fun broadcastTargets(): List<InetAddress> {
        val usbTether = mutableListOf<InetAddress>()
        val others = mutableListOf<InetAddress>()

        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            if (interfaces != null) {
                for (nif in interfaces) {
                    if (!nif.isUp || nif.isLoopback) continue
                    val bucket = if (looksLikeUsbTether(nif.name)) usbTether else others
                    for (addr in nif.interfaceAddresses) {
                        if (addr.address.isLoopbackAddress) continue
                        if (addr.address.address.size != 4) continue
                        addr.broadcast?.let { bucket.add(it) }
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "枚举网卡失败", e)
        }

        // 认得出 USB 网卡就只发给它，避免把发现包打到 WiFi 上去；
        // 认不出就退回"所有网卡都发"，用一点点噪音换连得上的确定性。
        return (if (usbTether.isNotEmpty()) usbTether else others)
            .ifEmpty { fallbackTargets() }
    }

    /** 接口名是否像 USB 网络共享。各厂商命名不一：`rndis0` / `usb0` / `ncm0`。 */
    private fun looksLikeUsbTether(name: String): Boolean {
        val n = name.lowercase()
        return n.startsWith("rndis") || n.startsWith("usb") || n.startsWith("ncm")
    }

    private fun fallbackTargets(): List<InetAddress> = try {
        listOf(InetAddress.getByName(TETHER_BROADCAST))
    } catch (e: Exception) {
        emptyList()
    }

    private companion object {
        const val TAG = "UdpLink"

        /**
         * 老版本 Android 的 USB 网络共享广播地址。
         *
         * 只在网卡枚举彻底失败时才用得上，属于最后一根稻草 ——
         * Android 12+ 早已不用这个网段，正常情况下走不到这里。
         */
        const val TETHER_BROADCAST = "192.168.42.255"

        /** 还没找到桥接程序时的发现包间隔。 */
        const val DISCOVER_ACTIVE_MS = 500L

        /** 已连上之后的保活间隔。 */
        const val DISCOVER_IDLE_MS = 2000L
    }
}
