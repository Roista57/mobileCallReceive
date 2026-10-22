package net.onebell.mcs.ui.settings

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class SettingsAction { START, TEST }
internal enum class SetupStep {
    ROLE, PERMISSIONS, BATTERY_CHECK, BATTERY_CHECKING, BATTERY,
    SAMSUNG_CHECK, SAMSUNG_CHECKING, SAMSUNG, EXECUTE
}
internal data class SetupRequest(val id: Long, val action: SettingsAction?, val step: SetupStep,
    val waiting: Boolean = false, val cancelled: Boolean = false)

/** Kept only in the ViewModel: rotation retains a request; a new process never resumes a pending start. */
internal class SetupFlow {
    private val mutable = MutableStateFlow<SetupRequest?>(null)
    val state = mutable.asStateFlow()
    val current get() = mutable.value
    private var sequence = 0L

    fun begin(action: SettingsAction?, step: SetupStep): Boolean {
        if (current != null) return false
        mutable.value = SetupRequest(++sequence, action, step)
        return true
    }
    fun move(expected: SetupRequest, step: SetupStep): SetupRequest? {
        if (current != expected || expected.cancelled) return null
        return expected.copy(step = step, waiting = false).also { mutable.value = it }
    }
    fun awaitExternal(expected: SetupRequest): SetupRequest? {
        if (current != expected || expected.waiting || expected.cancelled) return null
        return expected.copy(waiting = true).also { mutable.value = it }
    }
    fun externalResult(step: SetupStep): SetupRequest? {
        val request = current ?: return null
        if (request.step != step || !request.waiting) return null
        if (request.cancelled) { mutable.value = null; return null }
        return request
    }
    fun settingsReturned(step: SetupStep) {
        val request = externalResult(step) ?: return
        if (request.action == SettingsAction.START) move(request,
            if (step == SetupStep.BATTERY) SetupStep.SAMSUNG_CHECK else SetupStep.EXECUTE)
        else complete(request)
    }
    fun complete(expected: SetupRequest): Boolean {
        if (current != expected) return false
        mutable.value = null
        return !expected.cancelled
    }
    fun cancel() {
        val request = current ?: return
        // Keep an outstanding Activity Result reserved until it returns, so it cannot resume a newer start.
        mutable.value = if (request.waiting) request.copy(cancelled = true) else null
    }
}
