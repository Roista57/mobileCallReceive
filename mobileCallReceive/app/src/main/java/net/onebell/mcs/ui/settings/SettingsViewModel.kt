package net.onebell.mcs.ui.settings

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import net.onebell.mcs.McsApplication
import net.onebell.mcs.R
import net.onebell.mcs.call.CallAccess
import net.onebell.mcs.data.DeliveryStatus
import net.onebell.mcs.data.DeliveryLog
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import net.onebell.mcs.network.networkFailure
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel

data class SettingsState(
    val saved: AppConfig = AppConfig(),
    val draft: AppConfig = AppConfig(),
    val loaded: Boolean = false,
    val busy: Boolean = false,
    val role: Boolean = false,
    val contacts: Boolean = false,
    val localNetwork: Boolean = false,
    val batteryOptimizationIgnored: Boolean? = null,
    val samsung: Boolean = false,
    val setupPending: Boolean = false,
    val status: DeliveryStatus = DeliveryStatus(),
    val message: String = "",
    val storageWarning: String = "",
    val health: String = "미확인",
    val logs: List<DeliveryLog> = emptyList(),
) {
    val dirty: Boolean get() = draft.copy(enabled = false) != saved.copy(enabled = false)
}

class SettingsViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as McsApplication
    private val mutable = MutableStateFlow(SettingsState())
    val state = mutable.asStateFlow()
    private val messageChannel = Channel<String>(32, BufferOverflow.DROP_OLDEST)
    val messages = messageChannel.receiveAsFlow()
    internal val setupFlow = SetupFlow()
    internal val setupRequests = setupFlow.state
    private var setupNotice: String? = null

    init {
        viewModelScope.launch { setupRequests.collect { request ->
            mutable.update { it.copy(setupPending = request != null) }
        } }
        viewModelScope.launch {
            var readFailed = false
            var recoveryAnnounced = false
            app.settings.observeSnapshots {
                mutable.update { it.copy(loaded = false) }
                if (!readFailed) message("설정과 로그를 읽을 수 없습니다. 기존 데이터를 유지하고 자동으로 다시 시도합니다.")
                readFailed = true
            }.collect { snapshot ->
                val config = snapshot.config
                mutable.update { old -> old.copy(saved = config,
                    draft = if (!old.dirty) config else old.draft.copy(enabled = config.enabled),
                    loaded = true, status = snapshot.status, logs = snapshot.logs) }
                if (readFailed) { message("설정과 로그를 다시 읽었습니다."); readFailed = false }
                if (snapshot.recovered && !recoveryAnnounced) {
                    recoveryAnnounced = true
                    message("저장 데이터가 손상되어 기본값으로 복구했습니다. 이전 설정·로그를 복구하지 못했으며 서비스는 OFF입니다.")
                    try { app.settings.acknowledgeRecovery() }
                    catch (e: CancellationException) { throw e }
                    catch (_: Exception) { app.settings.reportRecordingFailure() }
                }
            }
        }
        viewModelScope.launch { app.settings.notices.filterNotNull().collect {
            mutable.update { old -> old.copy(storageWarning = it.text) }
            message(it.text)
        } }
        refreshAccess()
    }
    fun refreshAccess() {
        mutable.update { it.copy(role = CallAccess.hasRole(app), contacts = CallAccess.hasContacts(app),
            localNetwork = CallAccess.hasNetworkPermission(app),
            batteryOptimizationIgnored = BatterySettings.optimizationIgnored(app), samsung = BatterySettings.isSamsung) }
    }
    fun edit(config: AppConfig) { mutable.update { it.copy(draft = config, message = "") } }
    fun message(text: String) {
        mutable.update { it.copy(message = text) }
        if (text.isNotBlank()) {
            val warning = state.value.storageWarning
            messageChannel.trySend(if (warning.isNotBlank() && !text.contains(warning)) "$text\n$warning" else text)
        }
    }
    fun canRun(): Boolean {
        val current = state.value
        val error = when {
            !current.loaded -> "설정을 불러오는 중입니다."
            current.busy -> "진행 중인 작업을 기다려 주세요."
            setupFlow.current != null -> app.getString(R.string.setup_in_progress)
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
        if (setupFlow.current != null) return
        val config = state.value.draft
        config.validationError()?.let { message(it); return }
        run {
            try { app.settings.save(config) }
            catch (e: CancellationException) { throw e }
            catch (_: Exception) { message("설정을 저장하지 못했습니다. 저장 공간과 앱 저장소를 확인하세요."); return@run }
            mutable.update { it.copy(saved = config, draft = config, health = "미확인") }
            message("설정을 저장했습니다.")
        }
    }
    fun stop() {
        setupFlow.cancel()
        setupNotice = null
        run {
            app.calls.stop()
            message("중지했습니다.")
        }
    }
    internal fun beginSetup(action: SettingsAction) {
        if (!canRun()) return
        if (setupFlow.begin(action, SetupStep.ROLE)) setupNotice = null
    }
    internal fun openBatterySettings(samsung: Boolean = false) {
        if (state.value.busy) return
        if (setupFlow.begin(null, if (samsung) SetupStep.SAMSUNG else SetupStep.BATTERY)) setupNotice = null
    }
    internal fun setupWarning(text: String) {
        if (setupFlow.current?.action == SettingsAction.START) setupNotice = text
        else message(text)
    }
    internal fun permissionsFinished(request: SetupRequest) {
        if (setupFlow.current != request) return
        refreshAccess()
        if (request.action == SettingsAction.START && !state.value.role) {
            setupFlow.complete(request)
            message("Call Screening 역할이 필요합니다.")
        } else if (!state.value.localNetwork) {
            setupFlow.complete(request)
            message("로컬 네트워크 권한이 필요합니다. 앱 권한 설정을 확인하세요.")
        } else setupFlow.move(request, if (request.action == SettingsAction.START) SetupStep.BATTERY_CHECK else SetupStep.EXECUTE)
    }
    internal fun prepareBattery(request: SetupRequest) {
        val checking = setupFlow.move(request, SetupStep.BATTERY_CHECKING) ?: return
        viewModelScope.launch {
            val decision = batteryPromptDecision(app.settings::claimBatteryOptimizationPrompt) {
                BatterySettings.optimizationIgnored(app)
            }
            if (setupFlow.current != checking) return@launch
            if (decision.recordingFailed) setupWarning(app.getString(R.string.battery_prompt_record_failed))
            refreshAccess()
            setupFlow.move(checking,
                if (decision.request) SetupStep.BATTERY else SetupStep.EXECUTE)
        }
    }
    internal fun batterySettingsReturned(step: SetupStep) {
        refreshAccess()
        val request = setupFlow.externalResult(step) ?: return
        if (request.action == SettingsAction.START) setupFlow.move(request, SetupStep.EXECUTE)
        else setupFlow.complete(request)
    }
    internal fun finishSetup(request: SetupRequest) {
        if (setupFlow.complete(request)) request.action?.let(::execute)
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
                    val result = if (CallAccess.hasContacts(app)) "전화 수신 전달을 시작했습니다."
                        else "시작했습니다. 연락처 권한이 없어 저장된 번호의 감지가 제한됩니다."
                    message(listOfNotNull(result, setupNotice).joinToString("\n"))
                    setupNotice = null
                }
                SettingsAction.TEST -> {
                    val started = android.os.SystemClock.elapsedRealtime()
                    try {
                        val success = app.calls.sendEvent(CallEvent(phoneNumber = "01012345678", isTest = true))
                        val elapsed = android.os.SystemClock.elapsedRealtime() - started
                        mutable.update { it.copy(health = if (success) "성공 · ${elapsed}ms" else "실패") }
                        message(if (success) "PC 연결 및 테스트 이벤트 전송 성공"
                            else "전송에 실패하여 테스트 이벤트를 폐기했습니다. 로그의 상세 정보를 확인하세요.")
                    } catch (e: CancellationException) { throw e }
                    catch (e: Exception) {
                        mutable.update { it.copy(health = "실패") }
                        throw e
                    }
                }
            }
        }
    }
}
