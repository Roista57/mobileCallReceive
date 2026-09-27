package net.onebell.mcs.data

import android.content.Context
import androidx.datastore.preferences.core.*
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import net.onebell.mcs.model.AppConfig
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken

private val Context.settingsStore by preferencesDataStore("settings")

data class DeliveryStatus(val lastSent: String = "", val lastError: String = "", val lastWasTest: Boolean = false)
data class DeliveryLog(
    val receivedAt: String,
    val phoneNumber: String,
    val method: String,
    val destination: String,
    val success: Boolean,
    val result: String,
    val isTest: Boolean,
)

class SettingsRepository(context: Context) {
    private val store = context.settingsStore
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
    private val gson = Gson()
    private val logListType = object : TypeToken<List<DeliveryLog>>() {}.type
    val config = store.data.map { p ->
        val defaults = AppConfig()
        AppConfig(p[ip] ?: defaults.serverIp, p[port] ?: defaults.serverPort,
            p[path] ?: defaults.apiPath, p[enabled] ?: false)
    }
    val status = store.data.map { DeliveryStatus(it[lastSent] ?: "", it[lastError] ?: "", it[lastTest] ?: false) }
    val logs = store.data.map { preferences ->
        try { gson.fromJson<List<DeliveryLog>>(preferences[deliveryLogs] ?: "[]", logListType) ?: emptyList() }
        catch (_: Exception) { emptyList() }
    }
    suspend fun current() = config.first()
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
    suspend fun appendLog(log: DeliveryLog) {
        store.edit { preferences ->
            val current = try {
                gson.fromJson<List<DeliveryLog>>(preferences[deliveryLogs] ?: "[]", logListType) ?: emptyList()
            } catch (_: Exception) { emptyList() }
            preferences[deliveryLogs] = gson.toJson((listOf(log) + current).take(200))
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
