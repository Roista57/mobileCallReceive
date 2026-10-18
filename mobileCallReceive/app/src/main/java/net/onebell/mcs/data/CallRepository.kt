package net.onebell.mcs.data

import android.content.Context
import androidx.work.WorkManager
import androidx.work.await
import kotlinx.coroutines.CancellationException
import net.onebell.mcs.call.CallLog
import net.onebell.mcs.model.CallEvent
import net.onebell.mcs.network.CallApi
import net.onebell.mcs.network.DeliveryRunner
import net.onebell.mcs.network.networkSnapshot

class CallRepository(
    private val context: Context,
    private val settings: SettingsRepository,
    private val api: CallApi,
) {
    private val runner = DeliveryRunner(api) { networkSnapshot(context) }
    suspend fun sendEvent(event: CallEvent): Boolean {
        val config = settings.current()
        if (!event.isTest && !config.enabled) return false
        CallLog.event("HTTP_SEND_START", event.eventId)
        val result = runner.run(config, event, settings::upsertLog,
            onRecordFailure = { settings.reportRecordingFailure() }) { settings.current().enabled }
        // Persistence errors must not turn a successful HTTP request into another attempt.
        try {
            if (result.success) settings.success(result.attempts.last().sentAt.orEmpty(), event.isTest)
            else settings.error(result.result)
        } catch (e: CancellationException) { throw e }
        catch (_: Exception) { CallLog.event("STATUS_PERSIST_FAILED", event.eventId); settings.reportRecordingFailure() }
        CallLog.event(if (result.success) "HTTP_SEND_SUCCESS" else "HTTP_SEND_DISCARDED", event.eventId)
        return result.success
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
