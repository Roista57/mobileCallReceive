package net.onebell.mcs

import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.test.core.app.ApplicationProvider
import android.content.Context
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import net.onebell.mcs.data.*
import net.onebell.mcs.model.AppConfig
import org.junit.Test
import org.junit.Assert.*
import java.util.UUID

class StorageRecoveryDeviceTest {
    @Test fun androidFileStorageRecoversCorruptionAndPersistsAfterReopen() = runBlocking {
        val directory = ApplicationProvider.getApplicationContext<Context>().cacheDir.resolve("storage-test-" + UUID.randomUUID())
        directory.mkdirs()
        val file = directory.resolve("settings.preferences_pb")
        file.writeBytes(byteArrayOf(0xff.toByte(), 0xfe.toByte()))
        var job = SupervisorJob()
        fun repository() = SettingsRepository(PreferenceDataStoreFactory.create(
            corruptionHandler = settingsCorruptionHandler(), scope = CoroutineScope(job + Dispatchers.IO), produceFile = { file }))
        try {
            var settings = repository()
            assertTrue(settings.snapshots.first().recovered)
            assertFalse(settings.current().enabled)
            settings.acknowledgeRecovery()
            settings.save(AppConfig(serverIp = "192.168.0.99"))
            settings.setEnabled(true)
            settings.upsertLog(DeliveryLog(logId = "preserved", state = "SUCCESS", success = true))
            job.cancelAndJoin()
            job = SupervisorJob()
            settings = repository()
            assertEquals("192.168.0.99", settings.current().serverIp)
            assertTrue(settings.current().enabled)
            assertEquals("preserved", settings.snapshots.first().logs.single().logId)
            assertFalse(settings.snapshots.first().recovered)
        } finally { job.cancelAndJoin(); directory.deleteRecursively() }
    }
}
