package net.onebell.mcs

import androidx.datastore.core.DataStore
import androidx.datastore.core.okio.OkioStorage
import androidx.datastore.preferences.core.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import net.onebell.mcs.data.SettingsRepository
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.ui.settings.*
import okio.FileSystem
import okio.Path.Companion.toPath
import org.junit.Assert.*
import org.junit.Test
import org.junit.Rule
import org.junit.rules.TemporaryFolder
import java.io.IOException

class BatterySetupTest {
    @get:Rule val directory = TemporaryFolder()

    private fun store(file: java.io.File, job: Job) = PreferenceDataStoreFactory.create(
        storage = OkioStorage(FileSystem.SYSTEM, PreferencesSerializer, producePath = { file.absolutePath.toPath() }),
        scope = CoroutineScope(job + Dispatchers.IO))

    @Test fun firstPromptIsAtomicPersistsAndDoesNotChangeSettings() = runBlocking {
        val file = directory.root.resolve("settings.preferences_pb")
        var job = SupervisorJob()
        try {
            var settings = SettingsRepository(store(file, job))
            settings.save(AppConfig(serverIp = "192.168.0.50"))
            settings.setEnabled(true)
            assertEquals(1, (1..20).map { async { settings.claimBatteryOptimizationPrompt() } }.awaitAll().count { it })
            job.cancelAndJoin()
            job = SupervisorJob()
            settings = SettingsRepository(store(file, job))
            assertFalse(settings.claimBatteryOptimizationPrompt())
            assertTrue(settings.current().enabled)
            assertEquals("192.168.0.50", settings.current().serverIp)
        } finally { job.cancelAndJoin() }
    }

    @Test fun existingPreferencesWithoutFlagAreFirstStartAndDoNotEnableService() = runBlocking {
        val job = SupervisorJob()
        try {
            val data = store(directory.root.resolve("old.preferences_pb"), job)
            data.edit { it[stringPreferencesKey("serverIp")] = "192.168.0.51" }
            val settings = SettingsRepository(data)
            assertTrue(settings.claimBatteryOptimizationPrompt())
            assertFalse(settings.current().enabled)
            assertFalse(settings.claimBatteryOptimizationPrompt())
        } finally { job.cancelAndJoin() }
    }

    @Test fun alreadyExemptStillConsumesFirstCheckAndLaterRevocationDoesNotPrompt() = runBlocking {
        var attempted = false
        var ignored = true
        suspend fun claim(): Boolean = (!attempted).also { attempted = true }
        assertFalse(batteryPromptDecision(::claim) { ignored }.request)
        assertTrue(attempted)
        ignored = false
        assertFalse(batteryPromptDecision(::claim) { ignored }.request)
    }

    @Test fun denialDoesNotCauseAutomaticSecondPromptButManualRouteRemainsAvailable() = runBlocking {
        var attempted = false
        suspend fun claim(): Boolean = (!attempted).also { attempted = true }
        assertTrue(batteryPromptDecision(::claim) { false }.request)
        assertFalse(batteryPromptDecision(::claim) { false }.request)
        assertEquals(BatteryDestination.REQUEST_EXEMPTION, batteryDestinations(false, false).first())
        assertEquals(BatteryDestination.OPTIMIZATION_LIST, batteryDestinations(false, true).first())
    }

    @Test fun failedPersistentClaimSkipsPromptAndReportsFailure() = runBlocking {
        val failing = object : DataStore<Preferences> {
            override val data = flowOf(emptyPreferences())
            override suspend fun updateData(transform: suspend (Preferences) -> Preferences): Preferences = throw IOException("disk")
        }
        val decision = batteryPromptDecision(SettingsRepository(failing)::claimBatteryOptimizationPrompt) { false }
        assertFalse(decision.request)
        assertTrue(decision.recordingFailed)
    }

    @Test fun cancellationIsNotTreatedAsStorageFailure() = runBlocking {
        try {
            batteryPromptDecision({ throw CancellationException("cancelled") }) { false }
            fail("Expected cancellation")
        } catch (_: CancellationException) { }
    }

    @Test fun unknownStatusCanRequestButKnownExemptionCannot() = runBlocking {
        assertTrue(batteryPromptDecision({ true }) { null }.request)
        assertFalse(batteryPromptDecision({ true }) { true }.request)
    }

    @Test fun settingsFallbackStopsAfterFirstSuccessfulLaunch() {
        val attempts = mutableListOf<BatteryDestination>()
        val result = launchFirstAvailable(batteryDestinations(false, false)) {
            attempts += it
            if (it == BatteryDestination.REQUEST_EXEMPTION) throw SecurityException("blocked")
        }
        assertEquals(BatteryDestination.OPTIMIZATION_LIST, result)
        assertEquals(listOf(BatteryDestination.REQUEST_EXEMPTION, BatteryDestination.OPTIMIZATION_LIST), attempts)
        // Returning/declining does not itself run this helper again.
        assertEquals(2, attempts.size)
    }

    @Test fun allLaunchFailuresAndSamsungFallbackAreSafe() {
        assertNull(launchFirstAvailable(batteryDestinations(false, false)) { throw SecurityException("missing") })
        val attempts = mutableListOf<BatteryDestination>()
        assertEquals(BatteryDestination.APP_DETAILS, launchFirstAvailable(batteryDestinations(true, false)) {
            attempts += it
            if (it == BatteryDestination.SAMSUNG_EXCEPTIONS) throw SecurityException("missing")
        })
        assertEquals(listOf(BatteryDestination.SAMSUNG_EXCEPTIONS, BatteryDestination.APP_DETAILS), attempts)
    }

    @Test fun externalReturnCanCompleteStartOnlyOnceForApprovalOrDenial() {
        // Both native approval and cancellation take the same non-blocking return path.
        repeat(2) {
            val flow = SetupFlow()
            assertTrue(flow.begin(SettingsAction.START, SetupStep.BATTERY))
            val waiting = flow.awaitExternal(flow.current!!)!!
            assertNull(flow.awaitExternal(waiting)) // rotation must not launch the same request again
            assertFalse(flow.begin(SettingsAction.TEST, SetupStep.ROLE))
            assertEquals(waiting, flow.externalResult(SetupStep.BATTERY))
            val execute = flow.move(waiting, SetupStep.EXECUTE)!!
            assertNull(flow.externalResult(SetupStep.BATTERY))
            assertTrue(flow.complete(execute))
            assertFalse(flow.complete(execute))
        }
    }

    @Test fun stopWhileSettingsOpenCannotResumeOrConsumeANewerStart() {
        val flow = SetupFlow()
        flow.begin(SettingsAction.START, SetupStep.BATTERY)
        flow.awaitExternal(flow.current!!)
        flow.cancel()
        assertTrue(flow.current!!.cancelled)
        assertFalse(flow.begin(SettingsAction.START, SetupStep.ROLE))
        assertNull(flow.externalResult(SetupStep.BATTERY))
        assertNull(flow.current)
        assertTrue(flow.begin(SettingsAction.START, SetupStep.ROLE))
        assertNull(flow.externalResult(SetupStep.BATTERY))
        assertEquals(SetupStep.ROLE, flow.current!!.step)
    }

    @Test fun stopDuringFlagWriteInvalidatesItsContinuationAndNewProcessHasNoPendingStart() {
        val flow = SetupFlow()
        flow.begin(SettingsAction.START, SetupStep.BATTERY_CHECK)
        val checking = flow.move(flow.current!!, SetupStep.BATTERY_CHECKING)!!
        flow.cancel()
        flow.begin(SettingsAction.START, SetupStep.ROLE)
        assertNull(flow.move(checking, SetupStep.BATTERY))
        assertNull(SetupFlow().current)
    }
}
