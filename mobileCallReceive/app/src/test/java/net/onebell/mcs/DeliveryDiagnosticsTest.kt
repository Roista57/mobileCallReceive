package net.onebell.mcs

import com.google.gson.Gson
import com.google.gson.JsonParser
import kotlinx.coroutines.*
import net.onebell.mcs.data.*
import net.onebell.mcs.model.*
import net.onebell.mcs.network.*
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.*
import org.junit.*
import org.junit.Assert.*
import java.net.ConnectException
import java.net.SocketException
import java.net.UnknownHostException
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLHandshakeException

class DeliveryDiagnosticsTest {
    private lateinit var server: MockWebServer
    private lateinit var config: AppConfig
    private val event = CallEvent(phoneNumber = "01012345678", isTest = true)
    @Before fun setup() { server = MockWebServer(); server.start(); config = AppConfig(serverIp = "127.0.0.1", serverPort = server.port.toString()) }
    @After fun cleanup() { server.shutdown() }
    private fun api(ms: Long = 180) = CallApi(OkHttpClient.Builder().callTimeout(ms, TimeUnit.MILLISECONDS).build())
    private fun ok() = MockResponse().setBody("""{"success":true,"eventId":"${event.eventId}"}""")
    @Test fun timeoutConfiguration() {
        val c = CallApi().client
        assertEquals(5000, c.connectTimeoutMillis); assertEquals(5000, c.readTimeoutMillis)
        assertEquals(5000, c.writeTimeoutMillis); assertEquals(5000, c.callTimeoutMillis)
        assertFalse(c.retryOnConnectionFailure); assertFalse(c.followRedirects)
    }
    @Test fun productionCallTimeoutIsFiveSeconds() = runBlocking {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val start = System.nanoTime()
        val result = CallApi().sendAttempt(config, event)
        val elapsed = (System.nanoTime() - start) / 1_000_000
        assertEquals("TIMEOUT", result.failure!!.category)
        assertTrue("elapsed=$elapsed", elapsed in 4500..7500)
        assertEquals(1, server.requestCount)
    }
    @Test fun connectRefusedIsRetriedOnlyOnce() = runBlocking {
        val closed = java.net.ServerSocket(0).use { it.localPort }
        val result = DeliveryRunner(api(1000)).run(config.copy(serverPort = closed.toString()), event, {})
        assertFalse(result.success); assertEquals(2, result.attempts.size)
        assertTrue(result.attempts.all { it.category == "CONNECT" })
        assertTrue(result.attempts.all { it.localIp == null && it.remoteIp == null })
    }
    @Test fun readTimeoutIdentifiesResponseWaitingAndPreservesCause() = runBlocking {
        server.enqueue(MockResponse().setHeadersDelay(600, TimeUnit.MILLISECONDS).setBody("{}"))
        val client = OkHttpClient.Builder().readTimeout(100, TimeUnit.MILLISECONDS).callTimeout(2, TimeUnit.SECONDS).build()
        val result = CallApi(client).sendAttempt(config, event)
        assertEquals("TIMEOUT", result.failure!!.category)
        assertEquals("응답 대기", result.diagnostic.phase)
        assertTrue(result.diagnostic.errorType!!.contains("SocketTimeoutException"))
        assertTrue(result.diagnostic.causes.isNotEmpty())
    }
    @Test fun timeoutThenSuccessKeepsIdentityAndLogsBothAttempts() = runBlocking {
        server.enqueue(MockResponse().setHeadersDelay(600, TimeUnit.MILLISECONDS).setBody("{}")); server.enqueue(ok())
        val records = mutableListOf<DeliveryLog>()
        val result = DeliveryRunner(api()).run(config, event, { records += it })
        assertTrue(result.success); assertEquals(2, server.requestCount); assertEquals(2, result.attempts.size)
        val first = JsonParser.parseString(server.takeRequest().body.readUtf8()).asJsonObject
        val second = JsonParser.parseString(server.takeRequest().body.readUtf8()).asJsonObject
        assertEquals(first["eventId"], second["eventId"]); assertEquals(first["receivedAt"], second["receivedAt"])
        assertNotEquals(first["sentAt"], second["sentAt"])
        assertTrue(result.attempts[0].retryPlanned); assertEquals("TIMEOUT", result.attempts[0].category)
        assertEquals("127.0.0.1", result.attempts.last().localIp)
        assertEquals("127.0.0.1", result.attempts.last().remoteIp)
        assertEquals(200, result.attempts.last().httpStatus)
        assertTrue(result.attempts.last().steps.any { it.stage == "응답 대기" })
        assertEquals(1, records.map { it.logId }.distinct().size)
    }
    @Test fun twoTimeoutsEndWithoutThirdAttempt() = runBlocking {
        repeat(2) { server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE)) }
        val result = DeliveryRunner(api()).run(config, event, {})
        assertFalse(result.success); assertEquals(2, result.attempts.size); assertEquals("FAILED", result.state)
        delay(200); assertEquals(2, server.requestCount)
    }
    @Test fun httpAndProtocolFailuresNeverRetry() = runBlocking {
        for (code in listOf(400, 401, 403, 408, 429, 500, 503)) {
            server.enqueue(MockResponse().setResponseCode(code).setBody("{\"error\":\"failure\"}"))
            val result = DeliveryRunner(api(1000)).run(config, event, {})
            assertEquals(1, result.attempts.size); assertEquals("HTTP", result.attempts.single().category)
            assertTrue(result.attempts.single().responseText!!.contains("failure"))
        }
        for (body in listOf("not-json", "", "{", "{\"success\":true,\"eventId\":\"wrong\"}", "{}")) {
            server.enqueue(MockResponse().setBody(body))
            val result = DeliveryRunner(api(1000)).run(config, event, {})
            assertEquals(1, result.attempts.size); assertFalse(result.success)
        }
        assertEquals(12, server.requestCount)
    }
    @Test fun logWriteFailureDoesNotResendAcceptedEvent() = runBlocking {
        server.enqueue(ok())
        var warnings = 0
        assertTrue(DeliveryRunner(api(1000)).run(config, event, { throw java.io.IOException("disk") },
            onRecordFailure = { warnings++ }).success)
        assertEquals(1, warnings)
        assertEquals(1, server.requestCount)
    }
    @Test fun cancellationAfterAcknowledgementPreservesKnownSuccess() = runBlocking {
        server.enqueue(ok())
        val records = mutableListOf<DeliveryLog>()
        var cancelled = false
        try {
            DeliveryRunner(api(1000)).run(config, event, { log ->
                records += log
                if (log.success && !cancelled) { cancelled = true; throw CancellationException("cancelled during log persistence") }
            })
            fail("Expected cancellation")
        } catch (_: CancellationException) { }
        assertEquals("SUCCESS", records.last().state)
        assertTrue(records.last().success)
        assertEquals(1, server.requestCount)
    }
    @Test fun serverRetryAfterZeroDoesNotTriggerHiddenHttpRetry() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(503).addHeader("Retry-After", "0").setBody("busy"))
        server.enqueue(ok())
        val result = DeliveryRunner(api(1000)).run(config, event, {})
        assertFalse(result.success); assertEquals(503, result.attempts.single().httpStatus)
        assertEquals(1, server.requestCount)
    }
    @Test fun stopSuppressesRealRetryButManualTestStillWorks() = runBlocking {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val result = DeliveryRunner(api()).run(config, event.copy(isTest = false), {}, enabled = { false })
        assertEquals("STOPPED", result.state); assertEquals(1, result.attempts.size)
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE)); server.enqueue(ok())
        assertTrue(DeliveryRunner(api()).run(config, event, {}, enabled = { false }).success)
        assertEquals(3, server.requestCount)
    }
    @Test fun connectionErrorPolicy() {
        assertTrue(networkFailure(ConnectException()).retryable); assertTrue(networkFailure(SocketException("reset")).retryable)
        assertFalse(networkFailure(UnknownHostException()).retryable); assertFalse(networkFailure(SecurityException()).retryable)
        assertFalse(networkFailure(SSLHandshakeException("certificate")).retryable)
    }
    @Test fun interruptedBodyRetriesButMalformedJsonDoesNot() = runBlocking {
        server.enqueue(MockResponse().setBody("x".repeat(1000)).setSocketPolicy(SocketPolicy.DISCONNECT_DURING_RESPONSE_BODY)); server.enqueue(ok())
        val result = DeliveryRunner(api(1000)).run(config, event, {})
        assertTrue(result.success); assertTrue(result.attempts.first().responseIncomplete)
        assertEquals("TRANSPORT", result.attempts.first().category)
    }
    @Test fun healthRetryHasNoInventedPhoneOrEventId() = runBlocking {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        server.enqueue(MockResponse().setBody("""{"success":true,"status":"ok","version":"1.0.0"}"""))
        val result = DeliveryRunner(api()).run(config, null, {})
        assertTrue(result.success); assertEquals("HEALTH", result.kind); assertEquals("", result.phoneNumber)
        assertNull(result.eventId); assertEquals(2, result.attempts.size)
    }
    @Test fun cancellationRecordsUnknownWithoutRetry() = runBlocking {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val records = mutableListOf<DeliveryLog>()
        val job = launch { DeliveryRunner(api(2000)).run(config, event, { records += it }) }
        withContext(Dispatchers.IO) { server.takeRequest(2, TimeUnit.SECONDS) }
        job.cancelAndJoin(); delay(200)
        assertEquals(1, server.requestCount); assertEquals("UNKNOWN", records.last().state)
    }
    @Test fun responsePreviewIsBoundedButFullJsonIsValidated() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"success":true,"eventId":"${event.eventId}","extra":"${"x".repeat(5000)}"}"""))
        val result = DeliveryRunner(api(1000)).run(config, event, {})
        assertTrue(result.success); assertTrue(result.attempts.single().responseTruncated)
        assertEquals(4096, result.attempts.single().responseText!!.toByteArray().size)
    }
    @Test fun legacyLogsAndUnfinishedSessionsAreReadable() {
        val oldJson = """[{"receivedAt":"2020-01-01T00:00:00Z","phoneNumber":"123","success":true,"result":"ok"}]"""
        val old = DeliveryLogCodec.decode(oldJson).single()
        assertEquals("123", old.phoneNumber); assertTrue(old.success); assertTrue(old.attempts.isEmpty())
        assertEquals(old.logId, DeliveryLogCodec.decode(oldJson).single().logId)
        val pending = old.copy(logId = "pending", state = "SENDING", sessionId = "old-session")
        assertEquals("UNKNOWN", DeliveryLogCodec.decode(Gson().toJson(listOf(pending))).single().state)
        var logs = emptyList<DeliveryLog>()
        repeat(250) { logs = DeliveryLogCodec.upsert(logs, old.copy(logId = it.toString())) }
        assertEquals(200, logs.size)
        logs = DeliveryLogCodec.upsert(logs, logs[100].copy(result = "updated"))
        assertEquals(200, logs.size); assertEquals("updated", logs[100].result)
    }
    @Test fun concurrentCallsHaveSeparateTraces() = runBlocking {
        server.dispatcher = object : okhttp3.mockwebserver.Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val id = JsonParser.parseString(request.body.readUtf8()).asJsonObject["eventId"].asString
                return MockResponse().setBody("{\"success\":true,\"eventId\":\"$id\"}")
            }
        }
        val results = (1..10).map { async { DeliveryRunner(api(2000)).run(config, event.copy(eventId = java.util.UUID.randomUUID().toString()), {}) } }.awaitAll()
        assertTrue(results.all { it.success && it.attempts.size == 1 })
        assertEquals(10, results.map { it.logId }.distinct().size)
        results.forEach { assertTrue(it.attempts.single().requestJson!!.contains(it.eventId!!)) }
    }
}
