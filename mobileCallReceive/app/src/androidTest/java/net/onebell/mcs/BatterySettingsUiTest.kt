package net.onebell.mcs

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.content.ActivityNotFoundException
import android.os.ParcelFileDescriptor
import android.provider.Settings
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.Lifecycle
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.ui.settings.*
import org.junit.*
import org.junit.Assert.*
import java.util.concurrent.CopyOnWriteArrayList

/** Run on isolated emulator app data. Only the automatic-first-start case consumes the persisted first-start flag. */
class BatterySettingsUiTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val app get() = compose.activity.application as McsApplication
    private val vm get() = ViewModelProvider(compose.activity)[SettingsViewModel::class.java]
    private lateinit var saved: AppConfig
    private var ignoredBefore = false

    private fun shell(command: String): String = ParcelFileDescriptor.AutoCloseInputStream(
        instrumentation.uiAutomation.executeShellCommand(command)).bufferedReader().use { it.readText() }

    @Before fun setup() {
        saved = runBlocking { app.settings.current() }
        ignoredBefore = BatterySettings.optimizationIgnored(app) == true
        shell("cmd role add-role-holder android.app.role.CALL_SCREENING net.onebell.mcs")
        shell("pm grant net.onebell.mcs android.permission.READ_CONTACTS")
        if (android.os.Build.VERSION.SDK_INT >= 37) shell("pm grant net.onebell.mcs android.permission.ACCESS_LOCAL_NETWORK")
        shell("dumpsys deviceidle whitelist -net.onebell.mcs")
        runBlocking { app.settings.setEnabled(false) }
        compose.runOnIdle { vm.refreshAccess() }
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("서버 IP") and hasSetTextAction()).fetchSemanticsNodes().isNotEmpty() }
    }

    @After fun restore() {
        compose.runOnIdle { vm.setupFlow.cancel() }
        runBlocking { app.settings.save(saved); app.settings.setEnabled(saved.enabled) }
        if (ignoredBefore) shell("dumpsys deviceidle whitelist +net.onebell.mcs")
        else shell("dumpsys deviceidle whitelist -net.onebell.mcs")
    }

    private fun monitor(result: Int, requests: MutableList<Intent>) = object : Instrumentation.ActivityMonitor() {
        override fun onStartActivity(intent: Intent): Instrumentation.ActivityResult? {
            if (intent.action == Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS ||
                intent.action == Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS ||
                intent.action == "com.samsung.android.sm.ACTION_OPEN_CHECKABLE_LISTACTIVITY" ||
                intent.action == Settings.ACTION_APPLICATION_DETAILS_SETTINGS) {
                requests += Intent(intent)
                return Instrumentation.ActivityResult(result, null)
            }
            return null
        }
    }
    private fun awaitService(enabled: Boolean) {
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("서비스 사용: ${if (enabled) "ON" else "OFF"}")).fetchSemanticsNodes().isNotEmpty() }
        compose.waitUntil(10_000) { !vm.state.value.busy && !vm.state.value.setupPending }
    }

    @Test fun automaticRequestIsOnceAndCancellationStillStarts() {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = monitor(Activity.RESULT_CANCELED, requests)
        instrumentation.addMonitor(monitor)
        try {
            compose.onNodeWithText("시작", useUnmergedTree = true).performScrollTo().performClick()
            awaitService(true)
            assertEquals(1, requests.size)
            assertEquals(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, requests.single().action)
            assertEquals("package:net.onebell.mcs", requests.single().dataString)
            assertEquals(false, BatterySettings.optimizationIgnored(app))
            compose.onNodeWithText("중지").performScrollTo().performClick()
            awaitService(false)
            compose.activityRule.scenario.recreate()
            compose.onNodeWithText("시작", useUnmergedTree = true).performScrollTo().performClick()
            awaitService(true)
            assertEquals(1, requests.size)

        } finally {
            instrumentation.removeMonitor(monitor)
        }
    }

    @Test fun manualBatteryButtonIgnoresMisleadingActivityResultAndDoesNotStartService() {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = monitor(Activity.RESULT_OK, requests)
        instrumentation.addMonitor(monitor)
        try {
            compose.onNodeWithText("배터리 최적화: 적용 중").performScrollTo().assertIsDisplayed()
            compose.onNodeWithText("배터리 사용 설정 열기").performScrollTo().performClick()
            compose.waitUntil(10_000) { requests.size == 1 && !vm.state.value.setupPending }
            assertEquals(false, vm.state.value.batteryOptimizationIgnored)
            assertFalse(runBlocking { app.settings.current().enabled })
            compose.activityRule.scenario.recreate()
            compose.onNodeWithText("배터리 최적화: 적용 중").performScrollTo().assertIsDisplayed()
            if (!BatterySettings.isSamsung) compose.onNodeWithText("삼성 절전 예외 설정 열기").assertDoesNotExist()
        } finally { instrumentation.removeMonitor(monitor) }
    }

    @Test fun rotationDoesNotRelaunchPendingSettingsAndStopPreventsLaterStart() = verifyRotationAndStop(SetupStep.BATTERY)

    @Test fun samsungRotationDoesNotRelaunchAndStopPreventsLaterStart() = verifyRotationAndStop(SetupStep.SAMSUNG)

    private fun verifyRotationAndStop(step: SetupStep) {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = monitor(Activity.RESULT_CANCELED, requests)
        instrumentation.addMonitor(monitor)
        try {
            var waiting: SetupRequest? = null
            // External settings put the Activity below RESUMED. Reserve the result in that state.
            compose.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            compose.activityRule.scenario.onActivity { activity ->
                val model = ViewModelProvider(activity)[SettingsViewModel::class.java]
                model.setupFlow.begin(SettingsAction.START, step)
                waiting = model.setupFlow.awaitExternal(model.setupFlow.current!!)
            }
            compose.activityRule.scenario.recreate()
            compose.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            compose.runOnIdle { assertEquals(waiting, vm.setupFlow.current) }
            assertTrue(requests.isEmpty())
            compose.onNodeWithText("시작").performScrollTo().assertIsNotEnabled()
            compose.onNodeWithText("중지").performScrollTo().performClick()
            compose.waitUntil(10_000) { !vm.state.value.busy }
            compose.runOnIdle {
                vm.batterySettingsReturned(step)
                vm.batterySettingsReturned(step)
            }
            awaitService(false)
            assertNull(vm.setupFlow.current)
        } finally { instrumentation.removeMonitor(monitor) }
    }

    @Test fun alreadyExemptManualButtonOpensListAndRefreshesRealStatus() {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = monitor(Activity.RESULT_CANCELED, requests)
        instrumentation.addMonitor(monitor)
        try {
            shell("dumpsys deviceidle whitelist +net.onebell.mcs")
            compose.runOnIdle { vm.refreshAccess() }
            compose.onNodeWithText("배터리 최적화: 제외됨").performScrollTo().assertIsDisplayed()
            compose.onNodeWithText("배터리 사용 설정 열기").performScrollTo().performClick()
            compose.waitUntil(10_000) { requests.size == 1 && !vm.state.value.setupPending }
            assertEquals(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS, requests.single().action)
            assertEquals(true, vm.state.value.batteryOptimizationIgnored)
            assertFalse(runBlocking { app.settings.current().enabled })
        } finally { instrumentation.removeMonitor(monitor) }
    }

    // Pixel cannot provide One UI. Exercise the same Activity launch/return path with monitored Intents.
    private fun samsungMonitor(requests: MutableList<Intent>, failSamsung: Boolean = false,
        failAll: Boolean = false) = object : Instrumentation.ActivityMonitor() {
        override fun onStartActivity(intent: Intent): Instrumentation.ActivityResult? {
            if (intent.action == "com.samsung.android.sm.ACTION_OPEN_CHECKABLE_LISTACTIVITY" ||
                intent.action == Settings.ACTION_APPLICATION_DETAILS_SETTINGS) {
                requests += Intent(intent)
                if (failAll) throw SecurityException("Test: settings unavailable")
                if (failSamsung && intent.action != Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                    throw ActivityNotFoundException("Test: One UI unavailable")
                return Instrumentation.ActivityResult(Activity.RESULT_CANCELED, null)
            }
            return null
        }
    }

    @Test fun samsungScreenOpensEvenWhenAndroidExemptAndManualRouteStillWorks() {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = samsungMonitor(requests)
        instrumentation.addMonitor(monitor)
        try {
            shell("dumpsys deviceidle whitelist +net.onebell.mcs")
            compose.runOnIdle {
                vm.refreshAccess()
                vm.setupFlow.begin(SettingsAction.START, SetupStep.SAMSUNG)
            }
            awaitService(true)
            assertEquals(1, requests.size)
            assertEquals("com.samsung.android.lool", requests.single().`package`)
            assertEquals(2, requests.single().getIntExtra("activity_type", -1))
            compose.runOnIdle { vm.batterySettingsReturned(SetupStep.SAMSUNG) }
            assertEquals(1, requests.size)
            compose.onNodeWithText("중지").performScrollTo().performClick()
            awaitService(false)
            compose.runOnIdle { vm.openBatterySettings(samsung = true) }
            compose.waitUntil(10_000) { requests.size == 2 && !vm.state.value.setupPending }
            assertFalse(runBlocking { app.settings.current().enabled })
        } finally { instrumentation.removeMonitor(monitor) }
    }

    @Test fun samsungLaunchFailureUsesAppDetailsAndStillStarts() {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = samsungMonitor(requests, failSamsung = true)
        instrumentation.addMonitor(monitor)
        try {
            compose.runOnIdle { vm.setupFlow.begin(SettingsAction.START, SetupStep.SAMSUNG) }
            awaitService(true)
            assertEquals(2, requests.size)
            assertEquals(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, requests.last().action)
            assertEquals("package:net.onebell.mcs", requests.last().dataString)
            assertTrue(vm.state.value.message.contains("백그라운드 앱 사용 제한"))
        } finally { instrumentation.removeMonitor(monitor) }
    }

    @Test fun samsungAndAppDetailsLaunchFailuresStillStartWithWarning() {
        val requests = CopyOnWriteArrayList<Intent>()
        val monitor = samsungMonitor(requests, failAll = true)
        instrumentation.addMonitor(monitor)
        try {
            compose.runOnIdle { vm.setupFlow.begin(SettingsAction.START, SetupStep.SAMSUNG) }
            awaitService(true)
            assertEquals(2, requests.size)
            assertTrue(vm.state.value.message.contains("배터리 설정을 열 수 없습니다"))
            assertTrue(vm.state.value.message.contains("백그라운드 앱 사용 제한"))
        } finally { instrumentation.removeMonitor(monitor) }
    }

}
