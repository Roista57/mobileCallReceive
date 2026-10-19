package net.onebell.mcs.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.core.handlers.ReplaceFileCorruptionHandler
import androidx.datastore.preferences.core.*
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.retryWhen
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import net.onebell.mcs.model.AppConfig
import com.google.gson.Gson

internal val recoveredSettings = booleanPreferencesKey("corruptionRecovered")
internal fun settingsCorruptionHandler() = ReplaceFileCorruptionHandler<Preferences> {
    mutablePreferencesOf(recoveredSettings to true)
}
private val Context.settingsStore by preferencesDataStore("settings", corruptionHandler = settingsCorruptionHandler())

data class DeliveryStatus(val lastSent: String = "", val lastError: String = "", val lastWasTest: Boolean = false)
data class SettingsSnapshot(val config: AppConfig, val status: DeliveryStatus, val logs: List<DeliveryLog>, val recovered: Boolean)
data class StorageNotice(val id: Long, val text: String)
class SettingsRepository(private val store: DataStore<Preferences>) {
    constructor(context: Context) : this(context.settingsStore)
    private val noticeCounter = java.util.concurrent.atomic.AtomicLong()
    private val mutableNotice = kotlinx.coroutines.flow.MutableStateFlow<StorageNotice?>(null)
    val notices = mutableNotice.asStateFlow()
    private val ip = stringPreferencesKey("serverIp")
    private val port = stringPreferencesKey("serverPort")
    private val path = stringPreferencesKey("apiPath")
    private val legacyDevice = stringPreferencesKey("deviceId")
    private val legacyKey = stringPreferencesKey("apiKey")
    private val legacyQueueCleaned = booleanPreferencesKey("legacyQueueCleaned")
    private val enabled = booleanPreferencesKey("enabled")
    private val lastSent = stringPreferencesKey("lastSent")
    private val lastError = stringPreferencesKey("lastError")
    private val lastTest = booleanPreferencesKey("lastWasTest")
    private val deliveryLogs = stringPreferencesKey("deliveryLogs")
    private val batteryPromptAttempted = booleanPreferencesKey("batteryOptimizationPromptAttempted")
    private val gson = Gson()
    val config = store.data.map { p ->
        val defaults = AppConfig()
        AppConfig(p[ip] ?: defaults.serverIp, p[port] ?: defaults.serverPort,
            p[path] ?: defaults.apiPath, p[enabled] ?: false)
    }
    val status = store.data.map { DeliveryStatus(it[lastSent] ?: "", it[lastError] ?: "", it[lastTest] ?: false) }
    val logs = store.data.map { DeliveryLogCodec.decode(it[deliveryLogs] ?: "[]") }
    val snapshots = store.data.map { p ->
        val defaults = AppConfig()
        SettingsSnapshot(AppConfig(p[ip] ?: defaults.serverIp, p[port] ?: defaults.serverPort,
            p[path] ?: defaults.apiPath, p[enabled] ?: false),
            DeliveryStatus(p[lastSent] ?: "", p[lastError] ?: "", p[lastTest] ?: false),
            DeliveryLogCodec.decode(p[deliveryLogs] ?: "[]"), p[recoveredSettings] == true)
    }
    fun observeSnapshots(onReadFailure: (Throwable) -> Unit) = snapshots.retryWhen { error, _ ->
        if (error is CancellationException) throw error
        onReadFailure(error)
        delay(1500)
        true
    }
    fun reportRecordingFailure() {
        // Keep warnings independent of the failing DataStore.
        mutableNotice.value = StorageNotice(noticeCounter.incrementAndGet(),
            "전송 결과와 별개로 로그 또는 상태를 저장하지 못했습니다. 저장 공간과 앱 저장소를 확인하세요.")
    }
    suspend fun acknowledgeRecovery() { store.edit { it.remove(recoveredSettings) } }
    suspend fun current() = config.first()
    /** Claim atomically before opening any external UI, including when exemption already exists. */
    suspend fun claimBatteryOptimizationPrompt(): Boolean {
        var first = false
        store.edit { preferences ->
            first = preferences[batteryPromptAttempted] != true
            preferences[batteryPromptAttempted] = true
        }
        return first
    }
    suspend fun save(value: AppConfig) {
        require(value.validationError() == null)
        store.edit { p ->
            p[ip] = value.serverIp; p[port] = value.serverPort; p[path] = value.apiPath
        }
    }
    suspend fun setEnabled(value: Boolean) { store.edit { it[enabled] = value } }
    suspend fun error(message: String) { store.edit { it[lastError] = message } }
    suspend fun success(time: String, isTest: Boolean) {
        store.edit { it[lastSent] = time; it[lastError] = ""; it[lastTest] = isTest }
    }
    suspend fun upsertLog(log: DeliveryLog) {
        store.edit { preferences ->
            val current = DeliveryLogCodec.decode(preferences[deliveryLogs] ?: "[]")
            preferences[deliveryLogs] = gson.toJson(DeliveryLogCodec.upsert(current, log))
        }
    }
    suspend fun migrateLegacySettings(): Boolean {
        var cleanupNeeded = false
        store.edit { preferences ->
            preferences.remove(legacyDevice)
            preferences.remove(legacyKey)
            cleanupNeeded = preferences[legacyQueueCleaned] != true
        }
        return cleanupNeeded
    }
    suspend fun markLegacyQueueCleaned() {
        store.edit { it[legacyQueueCleaned] = true }
    }
}
