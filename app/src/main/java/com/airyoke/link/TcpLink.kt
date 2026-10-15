package com.airyoke.link

import android.util.Log
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.concurrent.thread

/**
 * 通道 B：adb reverse + TCP（自动回退）。
 *
 * PC 上执行 `adb reverse tcp:9000 tcp:9000` 之后，手机连**自己的** `127.0.0.1:9000`
 * 就会被转发到 PC 的 9000 端口。好处是手机侧零网络配置 —— 不用开网络共享、
 * 不用知道 PC 的 IP、不依赖任何网络环境，插上线就能用。
 *
 * 代价是它只支持 TCP，且 **adb 的端口映射会在锁屏/重插 USB 后失效**，
 * 保活与重建由 [LinkManager] 负责。
 *
 * ## 关于重连时的并发
 *
 * 这个类会反复 connect/close。**旧连接的接收线程在退出时只能清理它自己那条 socket**，
 * 不能无脑调 [close] —— 否则会把刚刚接手的新连接一起关掉，症状是"连上 1.5 秒后又断"。
 * 所以这里所有清理都先过 [isCurrent] 判断。
 */
class TcpLink(
    private val onTelemetry: (Protocol.Telemetry) -> Unit,
) {

    private var socket: Socket? = null
    private var output: OutputStream? = null

    @Volatile
    var lastReceiveMs: Long = 0L
        private set

    val isReady: Boolean
        get() = socket?.let { it.isConnected && !it.isClosed } == true

    /**
     * 尝试连到被 adb reverse 映射过来的本地端口。
     *
     * 注意 [Socket.tcpNoDelay] 必须在连接**之前**设置：Nagle 算法会把小的操纵包
     * 攒起来一起发，对这种每 10ms 一帧的数据流会造成明显的延迟抖动。
     */
    fun connect(timeoutMs: Int): Boolean {
        close()
        return try {
            val s = Socket()
            s.tcpNoDelay = true
            s.soTimeout = 0
            s.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), Protocol.PORT), timeoutMs)

            socket = s
            output = s.getOutputStream()
            lastReceiveMs = System.currentTimeMillis()

            val input = s.getInputStream()
            thread(name = "tcp-rx") { receiveLoop(s, input) }
            Log.i(TAG, "TCP 通道已连接 127.0.0.1:${Protocol.PORT}")
            true
        } catch (e: Exception) {
            // 连不上是常态：多半是没跑 adb reverse，或用户没开 USB 调试。
            // 这不是错误，只是这条通道不可用，交给 LinkManager 决定下一步。
            Log.i(TAG, "TCP 通道不可用：${e.message}")
            close()
            false
        }
    }

    fun send(bytes: ByteArray, length: Int): Boolean {
        val out = output ?: return false
        return try {
            out.write(bytes, 0, length)
            out.flush()
            true
        } catch (e: Exception) {
            Log.w(TAG, "TCP 发送失败，链路可能已断", e)
            close()
            false
        }
    }

    fun close() {
        val s = socket
        socket = null
        output = null
        try {
            s?.close()
        } catch (_: Exception) {
        }
    }

    /** 这条 socket 是否仍是当前连接。用于防止旧线程误关新连接。 */
    private fun isCurrent(candidate: Socket): Boolean = socket === candidate

    private fun receiveLoop(own: Socket, input: InputStream) {
        val buf = ByteArray(2048)

        while (isCurrent(own)) {
            try {
                val read = input.read(buf)
                if (read < 0) break

                if (isCurrent(own)) lastReceiveMs = System.currentTimeMillis()

                // TCP 是字节流，没有消息边界。当前桥接程序只回定长遥测包，
                // 但这里仍按"能解多少解多少"处理，避免将来升级协议时踩坑。
                var offset = 0
                while (offset + Protocol.TELEMETRY_SIZE <= read) {
                    Protocol.decodeTelemetry(buf, read, offset)?.let(onTelemetry)
                    offset += Protocol.TELEMETRY_SIZE
                }
            } catch (e: Exception) {
                if (isCurrent(own)) Log.w(TAG, "TCP 接收异常", e)
                break
            }
        }

        // 只有这条连接仍是"当前连接"时才清理，否则会误杀刚接手的新连接
        if (isCurrent(own)) {
            Log.i(TAG, "TCP 连接结束")
            close()
        }
    }

    private companion object {
        const val TAG = "TcpLink"
    }
}
