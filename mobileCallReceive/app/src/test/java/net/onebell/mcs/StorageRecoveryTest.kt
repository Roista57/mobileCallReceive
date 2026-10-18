package net.onebell.mcs

import androidx.datastore.core.DataStore
import androidx.datastore.core.okio.OkioStorage
import androidx.datastore.preferences.core.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import net.onebell.mcs.data.*
import net.onebell.mcs.model.AppConfig
import org.junit.*
import org.junit.Assert.*
import org.junit.rules.TemporaryFolder
import java.io.IOException
import okio.Path.Companion.toPath
import okio.FileSystem

class StorageRecoveryTest {
    @get:Rule val directory = TemporaryFolder()
    // Android's FileStorage uses File.renameTo; use the same serializer with portable atomic moves on Windows JVM.
    private fun createStore(file: java.io.File, job: Job) = PreferenceDataStoreFactory.create(
        storage = OkioStorage(FileSystem.SYSTEM, PreferencesSerializer, producePath = { file.absolutePath.toPath() }),
        corruptionHandler = settingsCorruptionHandler(), scope = CoroutineScope(job + Dispatchers.IO))

    @Test fun corruptStoreRecoversDisabledAndCanSaveAgain() = runBlocking {
        val file = directory.newFile("settings.preferences_pb")
        file.writeBytes(byteArrayOf(0xff.toByte(), 0xfe.toByte(), 0xff.toByte()))
        val job = SupervisorJob()
        try {
            val store = createStore(file, job)
            val repository = SettingsRepository(store)
            val recovered = repository.snapshots.first()
            assertTrue(recovered.recovered)
            assertFalse(recovered.config.enabled)
            assertTrue(recovered.logs.isEmpty())
            repository.acknowledgeRecovery()
            repository.save(AppConfig(serverIp = "192.168.0.50"))
            assertEquals("192.168.0.50", repository.current().serverIp)
            assertFalse(repository.current().enabled)
            assertFalse(repository.snapshots.first().recovered)
        } finally { job.cancelAndJoin() }
    }

    @Test fun readFailureDoesNotReplaceExistingPreferencesWithDefaults() = runBlocking {
        val existing = mutablePreferencesOf(stringPreferencesKey("serverIp") to "192.168.0.99",
            booleanPreferencesKey("enabled") to true)
        var subscriptions = 0
        var updates = 0
        val store = object : DataStore<Preferences> {
            override val data = flow<Preferences> {
                if (subscriptions++ == 0) throw IOException("temporary read failure")
                emit(existing)
            }
            override suspend fun updateData(transform: suspend (Preferences) -> Preferences): Preferences {
                updates++
                return transform(existing)
            }
        }
        val repository = SettingsRepository(store)
        try { repository.snapshots.first(); fail("Expected read error") } catch (_: IOException) { }
        subscriptions = 0
        var warnings = 0
        val restored = repository.observeSnapshots { warnings++ }.first()
        assertEquals(1, warnings)
        assertEquals(0, updates)
        assertEquals("192.168.0.99", restored.config.serverIp)
        assertTrue(restored.config.enabled)
    }

    @Test fun normalStoreRetainsSettingsStatusAndLogsAcrossReopen() = runBlocking {
        val file = directory.root.resolve("settings.preferences_pb")
        var job = SupervisorJob()
        try {
            var repository = SettingsRepository(createStore(file, job))
            repository.save(AppConfig(serverIp = "192.168.0.51"))
            repository.setEnabled(true)
            repository.success("2026-10-17T10:00:00+09:00", true)
            repository.upsertLog(DeliveryLog(logId = "one", success = true, state = "SUCCESS"))
            job.cancelAndJoin()
            job = SupervisorJob()
            repository = SettingsRepository(createStore(file, job))
            val snapshot = repository.snapshots.first()
            assertEquals("192.168.0.51", snapshot.config.serverIp)
            assertTrue(snapshot.config.enabled)
            assertTrue(snapshot.status.lastWasTest)
            assertEquals("one", snapshot.logs.single().logId)
        } finally { job.cancelAndJoin() }
    }
}
