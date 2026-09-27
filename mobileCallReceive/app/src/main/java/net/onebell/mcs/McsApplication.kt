package net.onebell.mcs

import android.app.Application
import kotlinx.coroutines.*
import net.onebell.mcs.call.CallLog
import net.onebell.mcs.data.*
import net.onebell.mcs.network.CallApi

class McsApplication : Application() {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    lateinit var settings: SettingsRepository
        private set
    lateinit var calls: CallRepository
        private set
    val api = CallApi()
    override fun onCreate() {
        super.onCreate()
        settings = SettingsRepository(this)
        calls = CallRepository(this, settings, api)
        scope.launch {
            try {
                if (settings.migrateLegacySettings()) {
                    calls.cleanupLegacyQueue()
                    settings.markLegacyQueueCleaned()
                }
            }
            catch (e: CancellationException) { throw e }
            catch (_: Exception) { CallLog.event("LEGACY_QUEUE_CLEANUP_FAILED") }
        }
    }
}
