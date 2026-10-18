package net.onebell.mcs.network

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import net.onebell.mcs.data.*
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import java.time.OffsetDateTime
import java.util.UUID

class DeliveryRunner(private val api: CallApi, private val snapshot: () -> NetworkSnapshot = { NetworkSnapshot() }) {
    suspend fun run(config: AppConfig, event: CallEvent?, record: suspend (DeliveryLog) -> Unit,
        onRecordFailure: (Throwable) -> Unit = {},
        enabled: suspend () -> Boolean = { true }): DeliveryLog {
        val health = event == null
        var log = DeliveryLog(receivedAt = event?.receivedAt.orEmpty(), phoneNumber = event?.phoneNumber.orEmpty(),
            method = if (health) "HTTP GET" else "HTTP POST",
            destination = "http://${config.serverIp}:${config.serverPort}${if (health) "/api/health" else config.apiPath}",
            isTest = event?.isTest ?: false, logId = UUID.randomUUID().toString(), schemaVersion = 2,
            eventId = event?.eventId, kind = if (health) "HEALTH" else "CALL", state = "SENDING",
            startedAt = OffsetDateTime.now().toString(), sessionId = DiagnosticSession.id)
        var recordingFailureReported = false
        suspend fun save() {
            try { record(log) } catch (e: CancellationException) { throw e } catch (error: Exception) {
                if (!recordingFailureReported) {
                    recordingFailureReported = true
                    runCatching { onRecordFailure(error) }
                }
            }
        }
        try {
            save()
            for (number in 1..2) {
                currentCoroutineContext().ensureActive()
                if (number == 2 && event?.isTest == false && !enabled()) {
                    log = log.copy(state = "STOPPED", result = "중지하여 재요청하지 않았습니다.", finishedAt = OffsetDateTime.now().toString())
                    save(); return log
                }
                val network = try { snapshot() } catch (e: CancellationException) { throw e }
                    catch (_: Exception) { NetworkSnapshot() }
                log = log.copy(attempts = log.attempts + DeliveryAttempt(number = number,
                    startedAt = OffsetDateTime.now().toString(), network = network))
                save()
                val attempt = if (health) api.healthAttempt(config, number, network) else api.sendAttempt(config, event!!, number, network)
                val retry = attempt.failure?.retryable == true && number == 1
                log = log.copy(attempts = log.attempts.dropLast(1) + attempt.diagnostic.copy(retryPlanned = retry),
                    success = attempt.failure == null, state = if (retry) "RETRYING" else if (attempt.failure == null) "SUCCESS" else "FAILED",
                    result = attempt.diagnostic.result, finishedAt = if (retry) null else OffsetDateTime.now().toString())
                save()
                if (!retry) return log
            }
            return log
        } catch (e: CancellationException) {
            if (log.state in listOf("SENDING", "RETRYING"))
                log = log.copy(state = "UNKNOWN", result = "요청이 취소되어 접수 여부를 확인할 수 없습니다.", finishedAt = OffsetDateTime.now().toString())
            withContext(NonCancellable) { save() }
            throw e
        } catch (e: Exception) {
            log = log.copy(state = "FAILED", success = false,
                result = networkFailure(e).userMessage, finishedAt = OffsetDateTime.now().toString())
            save()
            return log
        }
    }
}
