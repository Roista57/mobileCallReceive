package net.onebell.mcs.data

import com.google.gson.Gson
import com.google.gson.JsonParser
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID

object DiagnosticSession { val id: String = UUID.randomUUID().toString() }

data class NetworkSnapshot(val transport: String = "미확인")
data class TraceStep(val stage: String, val at: String, val elapsedMs: Long)
data class DeliveryAttempt(
    val number: Int = 1, val startedAt: String = "", val finishedAt: String = "",
    val elapsedMs: Long = 0, val sentAt: String? = null, val requestJson: String? = null,
    val requestTruncated: Boolean = false,
    val responseText: String? = null, val responseTruncated: Boolean = false,
    val responseIncomplete: Boolean = false, val responseInvalidUtf8: Boolean = false,
    val httpStatus: Int? = null, val phase: String = "요청 준비", val category: String? = null,
    val errorType: String? = null, val errorMessage: String? = null, val causes: List<String> = emptyList(),
    val network: NetworkSnapshot = NetworkSnapshot(), val localIp: String? = null,
    val remoteIp: String? = null, val remotePort: Int? = null, val steps: List<TraceStep> = emptyList(),
    val success: Boolean = false, val retryPlanned: Boolean = false, val result: String = "",
)
data class DeliveryLog(
    val receivedAt: String = "", val phoneNumber: String = "", val method: String = "HTTP POST",
    val destination: String = "", val success: Boolean = false, val result: String = "",
    val isTest: Boolean = false, val logId: String = "", val schemaVersion: Int = 1,
    val eventId: String? = null, val kind: String = "CALL", val state: String = "LEGACY",
    val startedAt: String = "", val finishedAt: String? = null,
    val sessionId: String? = null, val attempts: List<DeliveryAttempt> = emptyList(),
) {
    val stateText: String get() = when(state) {
        "SENDING" -> "전송 중"; "RETRYING" -> "재요청 중"; "SUCCESS" -> "성공"
        "FAILED" -> "실패"; "STOPPED" -> "중지로 재요청 취소"; "UNKNOWN" -> "완료 여부 확인 불가"
        else -> if (success) "성공" else "실패"
    }
}
object DeliveryLogCodec {
    private val gson = Gson()
    fun decode(json: String): List<DeliveryLog> = try {
        JsonParser.parseString(json).asJsonArray.mapIndexedNotNull { index, item ->
            try {
                val value = gson.fromJson(item, DeliveryLog::class.java)
                val id = value.logId.takeIf { it.isNotBlank() }
                    ?: UUID.nameUUIDFromBytes((index.toString() + item.toString()).toByteArray()).toString()
                value.copy(logId = id, state = if (value.state in listOf("SENDING", "RETRYING") &&
                    value.sessionId != DiagnosticSession.id) "UNKNOWN" else value.state)
            } catch (_: Exception) { null }
        }.take(200)
    } catch (_: Exception) { emptyList() }
    fun upsert(current: List<DeliveryLog>, log: DeliveryLog): List<DeliveryLog> {
        val index = current.indexOfFirst { it.logId == log.logId }
        return if (index < 0) (listOf(log) + current).take(200)
        else current.toMutableList().apply { this[index] = log }.take(200)
    }
}
fun diagnosticTime(value: String?): String = if (value.isNullOrBlank()) "기록 없음" else try {
    OffsetDateTime.parse(value).atZoneSameInstant(ZoneId.of("Asia/Seoul"))
        .format(DateTimeFormatter.ofPattern("yyyy.MM.dd HH:mm:ss.SSS"))
} catch (_: Exception) { value }
