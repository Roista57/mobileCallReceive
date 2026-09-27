package net.onebell.mcs.data

import android.content.Context
import androidx.work.WorkManager
import androidx.work.await
import kotlinx.coroutines.CancellationException
import net.onebell.mcs.call.CallLog
import net.onebell.mcs.model.CallEvent
import net.onebell.mcs.network.CallApi
import net.onebell.mcs.network.networkFailure

class CallRepository(
    private val context: Context,
    private val settings: SettingsRepository,
    private val api: CallApi,
) {
    suspend fun sendOnce(event: CallEvent): Boolean {
        val config = settings.current()
        if (!event.isTest && !config.enabled) return false
        val destination = "http://${config.serverIp}:${config.serverPort}${config.apiPath}"
        return try {
            CallLog.event("HTTP_SEND_START", event.eventId)
            val sentAt = api.send(config, event)
            settings.success(sentAt, event.isTest)
            CallLog.event("HTTP_SEND_SUCCESS", event.eventId)
            record(event, destination, true, "전송 성공")
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            val failure = networkFailure(e)
            settings.error(failure.userMessage)
            CallLog.event("HTTP_SEND_DISCARDED", "${event.eventId} ${failure.userMessage}")
            record(event, destination, false, failure.userMessage)
            false
        }
    }

    private suspend fun record(event: CallEvent, destination: String, success: Boolean, result: String) {
        try {
            settings.appendLog(DeliveryLog(event.receivedAt, event.phoneNumber, "HTTP POST", destination,
                success, result, event.isTest))
        } catch (_: Exception) {
            CallLog.event("DELIVERY_LOG_WRITE_FAILED", event.eventId)
        }
    }

    suspend fun start() = settings.setEnabled(true)
    suspend fun stop() = settings.setEnabled(false)

    suspend fun cleanupLegacyQueue() {
        val work = WorkManager.getInstance(context)
        work.cancelAllWorkByTag("real-calls").await()
        work.cancelAllWorkByTag("test-calls").await()
        work.cancelUniqueWork("queue-recovery").await()
        context.deleteDatabase("call-events.db")
        CallLog.event("LEGACY_QUEUE_REMOVED")
    }
}
