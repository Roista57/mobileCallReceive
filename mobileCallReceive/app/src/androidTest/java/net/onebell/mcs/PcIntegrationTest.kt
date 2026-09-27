package net.onebell.mcs

import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import org.junit.Assert.assertEquals
import org.junit.Assume
import org.junit.Test

class PcIntegrationTest {
    @Test fun emulatorToWindowsOneShotDelivery() = runBlocking {
        Assume.assumeTrue(InstrumentationRegistry.getArguments().getString("pcIntegration") == "true")
        val app = ApplicationProvider.getApplicationContext<McsApplication>()
        InstrumentationRegistry.getInstrumentation().uiAutomation
            .executeShellCommand("pm grant net.onebell.mcs android.permission.ACCESS_LOCAL_NETWORK").close()
        app.settings.save(AppConfig(serverIp = "10.0.2.2", enabled = true))
        assertEquals("ok", app.api.health(app.settings.current()).status)
        val event = CallEvent(phoneNumber = "01012345678", isTest = true)
        assertEquals(true, app.calls.sendOnce(event))
        app.api.send(app.settings.current(), event)
    }
}
