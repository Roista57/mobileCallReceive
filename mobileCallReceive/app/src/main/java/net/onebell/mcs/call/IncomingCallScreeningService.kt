package net.onebell.mcs.call

import android.telecom.Call
import android.telecom.CallScreeningService
import android.telecom.TelecomManager
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import net.onebell.mcs.McsApplication
import net.onebell.mcs.model.CallEvent
import java.time.OffsetDateTime
import java.util.UUID

fun incomingNumber(incoming: Boolean, restricted: Boolean, number: String?): String? =
    if (!incoming) null else if (restricted) "PRIVATE" else number?.takeIf { it.isNotBlank() } ?: "UNKNOWN"

class IncomingCallScreeningService : CallScreeningService() {
    override fun onScreenCall(details: Call.Details) {
        if (details.callDirection != Call.Details.DIRECTION_INCOMING) return
        val receivedAt = OffsetDateTime.now().toString()
        // Never wait for settings, disk or network before allowing the call.
        respondToCall(details, CallResponse.Builder()
            .setDisallowCall(false).setRejectCall(false).setSilenceCall(false)
            .setSkipCallLog(false).setSkipNotification(false).build())
        val id = UUID.randomUUID().toString()
        val number = incomingNumber(true,
            details.handlePresentation == TelecomManager.PRESENTATION_RESTRICTED,
            details.handle?.schemeSpecificPart) ?: return
        val app = application as McsApplication
        app.scope.launch {
            try {
                val config = app.settings.current()
                if (!config.enabled) return@launch
                CallLog.event("CALL_RECEIVED", id)
                CallLog.number(number)
                CallLog.event("CALL_RECEIVED_AT", receivedAt)
                app.calls.sendOnce(CallEvent(id, number, receivedAt))
            } catch (e: CancellationException) { throw e }
            catch (_: Exception) {
                CallLog.event("HTTP_SEND_DISCARDED", id)
                try { app.settings.error("전화 이벤트 전송 실패: 이벤트를 폐기했습니다.") }
                catch (e: CancellationException) { throw e }
                catch (_: Exception) { CallLog.event("STATUS_PERSIST_FAILED") }
            }
        }
    }
}
