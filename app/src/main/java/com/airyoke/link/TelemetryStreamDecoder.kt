package com.airyoke.link

/** 每条 TCP 连接独享缓冲；保留半包，坏字节逐个跳过，不累积无界数据。 */
class TelemetryStreamDecoder {
    private val pending = ByteArray(Protocol.TELEMETRY_SIZE)
    private var count = 0

    fun feed(bytes: ByteArray, length: Int, receive: (Protocol.Telemetry) -> Unit) {
        require(length in 0..bytes.size)
        for (i in 0 until length) {
            pending[count++] = bytes[i]
            if (count < pending.size) continue
            val decoded = Protocol.decodeTelemetry(pending, count)
            if (decoded != null) {
                count = 0
                receive(decoded)
            } else {
                pending.copyInto(pending, 0, 1, count)
                count--
            }
        }
    }
}
