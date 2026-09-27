package net.onebell.mcs

import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import net.onebell.mcs.model.AppConfig
import net.onebell.mcs.model.CallEvent
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test

class QueueWorkerTest {
    private lateinit var app: McsApplication
    private lateinit var server: MockWebServer
    private lateinit var saved: AppConfig

    @Before fun setup() = runBlocking {
        app = ApplicationProvider.getApplicationContext()
        saved = app.settings.current()
        InstrumentationRegistry.getInstrumentation().uiAutomation
            .executeShellCommand("pm grant net.onebell.mcs android.permission.ACCESS_LOCAL_NETWORK").close()
        server = MockWebServer()
        server.start()
        app.settings.save(AppConfig(serverIp = "127.0.0.1", serverPort = server.port.toString(), enabled = true))
    }

    @After fun cleanup() = runBlocking {
        server.shutdown()
        app.settings.save(saved)
    }

    @Test fun failedDeliveryIsAttemptedOnceAndNotQueued() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(503))
        assertFalse(app.calls.sendOnce(CallEvent(phoneNumber = "01012345678")))
        assertEquals(1, server.requestCount)
        assertFalse(app.getDatabasePath("call-events.db").exists())
    }

    @Test fun successUsesNewContractWithoutAuthentication() = runBlocking {
        val event = CallEvent(phoneNumber = "01012345678", isTest = true)
        server.enqueue(MockResponse().setBody("{\"success\":true,\"eventId\":\"" + event.eventId + "\"}"))
        assertTrue(app.calls.sendOnce(event))
        val request = server.takeRequest()
        assertNull(request.getHeader("X-Api-Key"))
        assertFalse(request.body.readUtf8().contains("deviceId"))
        assertEquals(1, server.requestCount)
    }

    @Test fun legacyDatabaseIsDeleted() = runBlocking {
        app.openOrCreateDatabase("call-events.db", 0, null).close()
        assertTrue(app.getDatabasePath("call-events.db").exists())
        app.calls.cleanupLegacyQueue()
        assertFalse(app.getDatabasePath("call-events.db").exists())
    }
}
