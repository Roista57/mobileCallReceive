package net.onebell.mcs

import com.google.gson.JsonParser
import kotlinx.coroutines.runBlocking
import net.onebell.mcs.model.*
import net.onebell.mcs.network.*
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.*
import org.junit.*
import org.junit.Assert.*
import java.util.concurrent.TimeUnit

class CallApiTest {
    private lateinit var server: MockWebServer
    private lateinit var config: AppConfig
    private val event = CallEvent(phoneNumber = "01012345678", isTest = true)
    @Before fun setUp() {
        server = MockWebServer()
        server.start()
        config = AppConfig(serverIp = "127.0.0.1", serverPort = server.port.toString())
    }
    @After fun tearDown() { server.shutdown() }
    @Test fun healthUsesFixedPathWithoutAuthentication() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"success":true,"status":"ok","version":"1.0.0"}"""))
        assertEquals("1.0.0", CallApi().health(config).version)
        val request = server.takeRequest()
        assertEquals("/api/health", request.path)
        assertNull(request.getHeader("X-Api-Key"))
    }
    @Test fun postUsesCustomPathAndExactContract() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"success":true,"eventId":"${event.eventId}"}"""))
        CallApi().send(config.copy(apiPath = "/custom/call"), event)
        val request = server.takeRequest()
        assertEquals("POST", request.method)
        assertEquals("/custom/call", request.path)
        assertNull(request.getHeader("X-Api-Key"))
        val body = JsonParser.parseString(request.body.readUtf8()).asJsonObject
        assertEquals(setOf("schemaVersion", "eventId", "phoneNumber", "receivedAt", "sentAt", "isTest"), body.keySet())
        assertEquals(event.eventId, body["eventId"].asString)
        assertTrue(body["isTest"].asBoolean)
    }
    private suspend fun expectFailure(retryable: Boolean, block: suspend () -> Unit) {
        try { block(); fail("Expected DeliveryFailure") }
        catch (e: DeliveryFailure) { assertEquals(retryable, e.retryable) }
    }
    @Test fun authFailureIsRetainedWithoutAutomaticRetry() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(401))
        expectFailure(false) { CallApi().send(config, event) }
    }
    @Test fun serverFailureIsRetryable() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(503))
        expectFailure(true) { CallApi().send(config, event) }
    }
    @Test fun wrongIdIsNotAcknowledged() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"success":true,"eventId":"wrong"}"""))
        expectFailure(false) { CallApi().send(config, event) }
    }
    @Test fun successFalseIsNotAcknowledged() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"success":false,"eventId":"${event.eventId}"}"""))
        expectFailure(false) { CallApi().send(config, event) }
    }
    @Test fun invalidJsonIsNotAcknowledged() = runBlocking {
        server.enqueue(MockResponse().setBody("not-json"))
        expectFailure(false) { CallApi().send(config, event) }
    }
    @Test fun timeoutIsRetryable() = runBlocking {
        server.enqueue(MockResponse().setHeadersDelay(1, TimeUnit.SECONDS).setBody("{}"))
        val client = OkHttpClient.Builder().callTimeout(100, TimeUnit.MILLISECONDS).build()
        expectFailure(true) { CallApi(client).send(config, event) }
    }
    @Test fun redirectsAreNotFollowed() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(302).addHeader("Location", "http://127.0.0.1:1/stolen"))
        expectFailure(false) { CallApi().send(config, event) }
        assertEquals(1, server.requestCount)
    }
}
