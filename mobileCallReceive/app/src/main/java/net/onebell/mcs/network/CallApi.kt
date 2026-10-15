package net.onebell.mcs.network

import com.google.gson.Gson
import com.google.gson.JsonParser
import kotlinx.coroutines.suspendCancellableCoroutine
import net.onebell.mcs.data.*
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import okio.Buffer
import java.io.IOException
import java.io.InterruptedIOException
import java.net.ConnectException
import java.net.SocketException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.time.OffsetDateTime
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException
import kotlin.coroutines.resume

class DeliveryFailure(val userMessage: String, val retryable: Boolean, val category: String = "PROTOCOL",
    cause: Throwable? = null) : Exception(userMessage, cause)

fun networkFailure(error: Throwable, phase: String = "요청"): DeliveryFailure = when (error) {
    is DeliveryFailure -> error
    is UnknownHostException -> DeliveryFailure("잘못된 IP 또는 주소를 확인하세요.", false, "ADDRESS", error)
    is SecurityException -> DeliveryFailure("로컬 네트워크 권한을 확인하세요.", false, "PERMISSION", error)
    is SSLException -> DeliveryFailure("TLS 연결/인증서 오류입니다.", false, "TLS", error)
    is java.net.ProtocolException -> if (error.message == "unexpected end of stream")
        DeliveryFailure("응답 전송 도중 연결이 끊겼습니다: $phase", true, "TRANSPORT", error)
        else DeliveryFailure("HTTP 프로토콜 형식 오류입니다.", false, "RESPONSE_FORMAT", error)
    is java.net.UnknownServiceException -> DeliveryFailure("네트워크 구성 또는 지원하지 않는 연결 방식입니다.", false, "CONFIG", error)
    is SocketTimeoutException -> DeliveryFailure("Timeout: $phase 시간이 초과되었습니다.", true, "TIMEOUT", error)
    is InterruptedIOException -> DeliveryFailure("Timeout: 전체 요청 또는 $phase 시간이 초과되었습니다.", true, "TIMEOUT", error)
    is ConnectException -> DeliveryFailure("Connection refused/연결 실패: PC 실행·주소·방화벽을 확인하세요.", true, "CONNECT", error)
    is SocketException -> DeliveryFailure("연결이 끊겼습니다: $phase", true, "TRANSPORT", error)
    is IOException -> DeliveryFailure("네트워크 전송 오류: $phase", true, "TRANSPORT", error)
    else -> DeliveryFailure("전송 처리 오류: 설정과 서버 응답을 확인하세요.", false, "PROCESSING", error)
}
fun httpFailure(code: Int) = DeliveryFailure("HTTP 오류: $code", false, "HTTP")
data class ApiAttempt<T>(val value: T?, val failure: DeliveryFailure?, val diagnostic: DeliveryAttempt)

class CallApi(client: OkHttpClient? = null, private val scheme: String = "http") {
    val client: OkHttpClient = (client?.newBuilder() ?: OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS).readTimeout(5, TimeUnit.SECONDS)
        .writeTimeout(5, TimeUnit.SECONDS).callTimeout(5, TimeUnit.SECONDS))
        .retryOnConnectionFailure(false).followRedirects(false).followSslRedirects(false)
        .addNetworkInterceptor { chain ->
            val response = chain.proceed(chain.request())
            // OkHttp 4.12 follows 503 + Retry-After: 0 even with connection retries disabled.
            // Override only the local retry hint; never change the request or the response payload.
            if (response.code == 503 && response.header("Retry-After")?.toIntOrNull() == 0)
                response.newBuilder().header("Retry-After", "1").build() else response
        }
        .eventListenerFactory { call -> call.request().tag(DeliveryTrace::class.java) ?: EventListener.NONE }.build()
    private val gson = Gson()
    private fun base(config: AppConfig): String {
        config.validationError()?.let { throw DeliveryFailure(it, false, "CONFIG") }
        return "$scheme://${config.serverIp}:${config.serverPort}"
    }
    suspend fun health(config: AppConfig): HealthResponse = healthAttempt(config).let { it.value ?: throw it.failure!! }
    suspend fun send(config: AppConfig, event: CallEvent): String = sendAttempt(config, event).let { it.value ?: throw it.failure!! }
    suspend fun sendAttempt(config: AppConfig, event: CallEvent, number: Int = 1,
        snapshot: NetworkSnapshot = NetworkSnapshot()): ApiAttempt<String> {
        val sentAt = OffsetDateTime.now().toString()
        val json = gson.toJson(CallEventRequest.from(event, sentAt))
        return attempt(config, "POST", config.apiPath, json, sentAt, number, snapshot) { text ->
            val root = JsonParser.parseString(text).asJsonObject
            if (root["success"]?.isJsonPrimitive != true || !root["success"].asJsonPrimitive.isBoolean ||
                !root["success"].asBoolean || root["eventId"]?.isJsonPrimitive != true ||
                !root["eventId"].asJsonPrimitive.isString || root["eventId"].asString != event.eventId)
                throw DeliveryFailure("서버 응답 success/eventId가 일치하지 않습니다.", false, "RESPONSE_VALIDATION")
            sentAt
        }
    }
    suspend fun healthAttempt(config: AppConfig, number: Int = 1,
        snapshot: NetworkSnapshot = NetworkSnapshot()): ApiAttempt<HealthResponse> =
        attempt(config, "GET", "/api/health", null, null, number, snapshot) { text ->
            val root = JsonParser.parseString(text).asJsonObject
            if (root["success"]?.isJsonPrimitive != true || !root["success"].asJsonPrimitive.isBoolean ||
                !root["success"].asBoolean || root["status"]?.asString != "ok" || root["version"]?.asString.isNullOrBlank())
                throw DeliveryFailure("서버 health 응답 형식이 올바르지 않습니다.", false, "RESPONSE_VALIDATION")
            gson.fromJson(root, HealthResponse::class.java)
        }

    private suspend fun <T> attempt(config: AppConfig, method: String, path: String, json: String?, sentAt: String?,
        number: Int, snapshot: NetworkSnapshot, parse: (String) -> T): ApiAttempt<T> {
        val trace = DeliveryTrace(number, snapshot, json, sentAt)
        val request = try {
            Request.Builder().url(base(config) + path).tag(DeliveryTrace::class.java, trace)
                .method(method, json?.toRequestBody("application/json; charset=utf-8".toMediaType())).build()
        } catch (error: Exception) {
            val failure = networkFailure(error)
            return ApiAttempt(null, failure, trace.finish(failure))
        }
        return suspendCancellableCoroutine { continuation ->
            val call = client.newCall(request)
            continuation.invokeOnCancellation { call.cancel() }
            call.enqueue(object : Callback {
                override fun onFailure(call: Call, e: IOException) {
                    val failure = networkFailure(e, trace.finish(null).phase)
                    if (continuation.isActive) continuation.resume(ApiAttempt(null, failure, trace.finish(failure)))
                }
                override fun onResponse(call: Call, response: Response) {
                    val buffer = Buffer()
                    var incomplete = true
                    var result: T? = null
                    var failure: DeliveryFailure? = null
                    fun capture() {
                        val bytes = buffer.snapshot().toByteArray()
                        val preview = bytes.copyOfRange(0, minOf(bytes.size, 4096))
                        val invalid = try {
                            Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                                .decode(ByteBuffer.wrap(bytes)); false
                        } catch (_: Exception) { true }
                        trace.response(response.code, preview.toString(Charsets.UTF_8), bytes.size > 4096,
                            incomplete, invalid)
                    }
                    response.use {
                        try {
                            val source = response.body?.source()
                            if (source != null) while (buffer.size <= 65536) {
                                if (source.read(buffer, minOf(8192L, 65537L - buffer.size)) == -1L) { incomplete = false; break }
                            } else incomplete = false
                            capture()
                            if (!response.isSuccessful) throw httpFailure(response.code)
                            if (buffer.size > 65536) throw DeliveryFailure("서버 응답이 너무 큽니다.", false, "RESPONSE_FORMAT")
                            trace.phase("응답 검증")
                            try { result = parse(buffer.clone().readUtf8()) }
                            catch (e: DeliveryFailure) { throw e }
                            catch (e: Exception) { throw DeliveryFailure("서버 JSON 응답 형식이 올바르지 않습니다.", false, "RESPONSE_FORMAT", e) }
                        } catch (e: Exception) {
                            capture()
                            // An HTTP status already received is a protocol result, even if its body is interrupted.
                            failure = if (!response.isSuccessful) httpFailure(response.code) else networkFailure(e, trace.finish(null).phase)
                        }
                    }
                    if (continuation.isActive) continuation.resume(ApiAttempt(result, failure, trace.finish(failure)))
                }
            })
        }
    }
}
