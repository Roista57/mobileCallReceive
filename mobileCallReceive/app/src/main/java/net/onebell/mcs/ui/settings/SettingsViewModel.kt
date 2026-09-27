package net.onebell.mcs.ui.settings

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import net.onebell.mcs.McsApplication
import net.onebell.mcs.call.CallAccess
import net.onebell.mcs.call.CallLog
import net.onebell.mcs.data.DeliveryStatus
import net.onebell.mcs.data.DeliveryLog
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import net.onebell.mcs.network.networkFailure

enum class SettingsAction { START, HEALTH, TEST }

data class SettingsState(
    val saved: AppConfig = AppConfig(),
    val draft: AppConfig = AppConfig(),
    val loaded: Boolean = false,
    val busy: Boolean = false,
    val role: Boolean = false,
    val contacts: Boolean = false,
    val localNetwork: Boolean = false,
    val status: DeliveryStatus = DeliveryStatus(),
    val message: String = "",
    val health: String = "미확인",
    val logs: List<DeliveryLog> = emptyList(),
) {
    val dirty: Boolean get() = draft.copy(enabled = false) != saved.copy(enabled = false)
}

class SettingsViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as McsApplication
    private val mutable = MutableStateFlow(SettingsState())
    val state = mutable.asStateFlow()
    var pendingAction: SettingsAction? = null

    init {
        viewModelScope.launch {
            app.settings.config.catch { message("설정을 읽을 수 없습니다.") }.collect { config ->
                mutable.update { old -> old.copy(saved = config,
                    draft = if (!old.loaded || !old.dirty) config else old.draft.copy(enabled = config.enabled),
                    loaded = true) }
            }
        }
        viewModelScope.launch { app.settings.status.collect { status -> mutable.update { it.copy(status = status) } } }
        viewModelScope.launch { app.settings.logs.collect { logs -> mutable.update { it.copy(logs = logs) } } }
        refreshAccess()
    }
    fun refreshAccess() {
        mutable.update { it.copy(role = CallAccess.hasRole(app), contacts = CallAccess.hasContacts(app),
            localNetwork = CallAccess.hasNetworkPermission(app)) }
    }
    fun edit(config: AppConfig) { mutable.update { it.copy(draft = config, message = "") } }
    fun message(text: String) { mutable.update { it.copy(message = text) } }
    fun canRun(): Boolean {
        val current = state.value
        val error = when {
            !current.loaded -> "설정을 불러오는 중입니다."
            current.busy -> "진행 중인 작업을 기다려 주세요."
            current.dirty -> "변경한 설정을 먼저 저장하세요."
            else -> current.saved.validationError()
        }
        if (error != null) message(error)
        return error == null
    }
    private fun run(block: suspend () -> Unit) {
        if (state.value.busy) return
        mutable.update { it.copy(busy = true, message = "") }
        viewModelScope.launch {
            try { block() }
            catch (e: CancellationException) { throw e }
            catch (e: Exception) { message(networkFailure(e).userMessage) }
            finally { mutable.update { it.copy(busy = false) } }
        }
    }
    fun save() {
        val config = state.value.draft
        config.validationError()?.let { message(it); return }
        run {
            app.settings.save(config)
            mutable.update { it.copy(saved = config, draft = config, health = "미확인") }
            message("설정을 저장했습니다.")
        }
    }
    fun stop() = run {
        app.calls.stop()
        message("중지했습니다.")
    }
    fun execute(action: SettingsAction) {
        refreshAccess()
        if (!canRun()) return
        if (!state.value.localNetwork) { message("로컬 네트워크 권한이 필요합니다. 앱 권한 설정을 확인하세요."); return }
        run {
            when (action) {
                SettingsAction.START -> {
                    if (!CallAccess.hasRole(app)) { message("Call Screening 역할이 필요합니다."); return@run }
                    app.calls.start()
                    message(if (CallAccess.hasContacts(app)) "전화 수신 전달을 시작했습니다."
                        else "시작했습니다. 연락처 권한이 없어 저장된 번호의 감지가 제한됩니다.")
                }
                SettingsAction.HEALTH -> {
                    val started = android.os.SystemClock.elapsedRealtime()
                    try {
                        val response = app.api.health(app.settings.current())
                        val elapsed = android.os.SystemClock.elapsedRealtime() - started
                        mutable.update { it.copy(health = "성공 · ${elapsed}ms") }
                        message("PC 연결 성공")
                        CallLog.event("HEALTH_CHECK_SUCCESS")
                    } catch (e: CancellationException) { throw e }
                    catch (e: Exception) {
                        mutable.update { it.copy(health = "실패") }
                        CallLog.event("HEALTH_CHECK_FAILED")
                        throw e
                    }
                }
                SettingsAction.TEST -> {
                    val success = app.calls.sendOnce(CallEvent(phoneNumber = "01012345678", isTest = true))
                    message(if (success) "테스트 이벤트 전송 성공" else "전송에 실패하여 테스트 이벤트를 폐기했습니다.")
                }
            }
        }
    }
}
