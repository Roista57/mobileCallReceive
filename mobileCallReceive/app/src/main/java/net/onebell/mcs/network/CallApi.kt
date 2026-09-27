package net.onebell.mcs.network

import com.google.gson.JsonParseException
import com.google.gson.stream.MalformedJsonException
import kotlinx.coroutines.CancellationException
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import okhttp3.OkHttpClient
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.io.IOException
import java.io.EOFException
import java.io.InterruptedIOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.OffsetDateTime
import java.util.concurrent.TimeUnit

class DeliveryFailure(val userMessage: String, val retryable: Boolean) : Exception(userMessage)

fun networkFailure(error: Throwable): DeliveryFailure = when (error) {
    is DeliveryFailure -> error
    is SocketTimeoutException -> DeliveryFailure("Timeout: PC 응답 시간이 초과되었습니다.", true)
    is InterruptedIOException -> DeliveryFailure("Timeout: 요청 시간이 초과되었습니다.", true)
    is ConnectException -> DeliveryFailure("Connection refused/연결 실패: PC 실행·주소·방화벽을 확인하세요.", true)
    is UnknownHostException -> DeliveryFailure("잘못된 IP 또는 주소를 확인하세요.", false)
    is JsonParseException -> DeliveryFailure("서버 JSON 응답 형식이 올바르지 않습니다.", false)
    is MalformedJsonException, is EOFException -> DeliveryFailure("서버 JSON 응답이 잘못되었거나 비어 있습니다.", false)
    is SecurityException -> DeliveryFailure("로컬 네트워크 권한을 확인하세요.", false)
    is IOException -> DeliveryFailure("기타 네트워크 오류: Wi-Fi와 PC 연결을 확인하세요.", true)
    else -> DeliveryFailure("전송 처리 오류: 설정과 서버 응답을 확인하세요.", false)
}
fun httpFailure(code: Int) = DeliveryFailure(
    if (code == 401 || code == 403) "인증 실패 (HTTP $code): API Key를 확인하세요."
    else "HTTP 오류: $code", code == 408 || code == 429 || code in 500..599)

class CallApi(
    private val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS).readTimeout(10, TimeUnit.SECONDS)
        .callTimeout(15, TimeUnit.SECONDS).followRedirects(false).followSslRedirects(false).build(),
    private val scheme: String = "http",
) {
    private val service = Retrofit.Builder().baseUrl("http://localhost/")
        .client(client).addConverterFactory(GsonConverterFactory.create()).build().create(ApiService::class.java)
    private fun base(config: AppConfig): String {
        config.validationError()?.let { throw DeliveryFailure(it, false) }
        return "$scheme://${config.serverIp}:${config.serverPort}"
    }
    suspend fun health(config: AppConfig): HealthResponse = guarded {
        val response = service.health(base(config) + "/api/health")
        if (!response.isSuccessful) throw httpFailure(response.code())
        val body = response.body()
        if (body?.success != true || body.status != "ok" || body.version.isNullOrBlank())
            throw DeliveryFailure("서버 health 응답 형식이 올바르지 않습니다.", false)
        body
    }
    suspend fun send(config: AppConfig, event: CallEvent): String = guarded {
        val sentAt = OffsetDateTime.now().toString()
        val response = service.send(base(config) + config.apiPath, CallEventRequest.from(event, sentAt))
        if (!response.isSuccessful) throw httpFailure(response.code())
        if (response.body()?.success != true || response.body()?.eventId != event.eventId)
            throw DeliveryFailure("서버 응답 success/eventId가 일치하지 않습니다.", false)
        sentAt
    }
    private suspend fun <T> guarded(block: suspend () -> T): T = try { block() }
    catch (e: CancellationException) { throw e }
    catch (e: Exception) { throw networkFailure(e) }
}
