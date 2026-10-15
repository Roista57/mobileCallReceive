package net.onebell.mcs.network

import net.onebell.mcs.data.*
import okhttp3.*
import java.net.InetSocketAddress
import java.net.Proxy
import java.time.OffsetDateTime

internal class DeliveryTrace(number: Int, network: NetworkSnapshot, requestJson: String?, sentAt: String?) : EventListener() {
    private val started = System.nanoTime()
    private val steps = mutableListOf<TraceStep>()
    private var value = DeliveryAttempt(number = number, startedAt = OffsetDateTime.now().toString(),
        network = network, requestJson = requestJson?.toByteArray(Charsets.UTF_8)?.take(16384)?.toByteArray()?.toString(Charsets.UTF_8),
        requestTruncated = (requestJson?.toByteArray(Charsets.UTF_8)?.size ?: 0) > 16384, sentAt = sentAt)
    @Synchronized fun phase(stage: String) {
        if (steps.size < 100) steps += TraceStep(stage, OffsetDateTime.now().toString(), (System.nanoTime() - started) / 1_000_000)
        value = value.copy(phase = stage)
    }
    @Synchronized fun response(code: Int, text: String, truncated: Boolean, incomplete: Boolean, invalidUtf8: Boolean) {
        value = value.copy(httpStatus = code, responseText = text, responseTruncated = truncated,
            responseIncomplete = incomplete, responseInvalidUtf8 = invalidUtf8)
    }
    @Synchronized fun finish(failure: DeliveryFailure?): DeliveryAttempt = value.copy(
        finishedAt = OffsetDateTime.now().toString(), elapsedMs = (System.nanoTime() - started) / 1_000_000,
        steps = steps.toList(), success = failure == null, result = failure?.userMessage ?: "PC 접수 확인 성공",
        category = failure?.category, errorType = failure?.cause?.javaClass?.name,
        errorMessage = failure?.cause?.message,
        causes = generateSequence(failure?.cause) { it.cause }.take(8).map { "${it.javaClass.name}: ${it.message.orEmpty()}" }.toList())
    override fun dnsStart(call: Call, domainName: String) = phase("주소 조회")
    override fun connectStart(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy) = phase("서버 연결")
    override fun secureConnectStart(call: Call) = phase("TLS 연결")
    override fun connectionAcquired(call: Call, connection: Connection) {
        val socket = connection.socket()
        synchronized(this) {
            value = value.copy(localIp = socket.localAddress?.hostAddress,
                remoteIp = socket.inetAddress?.hostAddress, remotePort = socket.port)
        }
        phase("연결 획득")
    }
    override fun requestHeadersStart(call: Call) = phase("요청 헤더 전송")
    override fun requestBodyStart(call: Call) = phase("요청 본문 전송")
    override fun requestBodyEnd(call: Call, byteCount: Long) = phase("응답 대기")
    override fun requestHeadersEnd(call: Call, request: Request) { if (request.body == null) phase("응답 대기") }
    override fun responseHeadersEnd(call: Call, response: Response) = phase("응답 본문 읽기")
}
