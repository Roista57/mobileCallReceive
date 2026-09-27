package net.onebell.mcs

import net.onebell.mcs.call.incomingNumber
import net.onebell.mcs.model.*
import net.onebell.mcs.network.*
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException
import java.time.OffsetDateTime
import java.util.UUID

class ConfigAndPolicyTest {
    private val config = AppConfig()
    @Test fun validConfig() { assertNull(config.validationError()) }
    @Test fun invalidIpAndPorts() {
        listOf("host", "192.168.1.256", "192.168.01.1", "127.0.0", "").forEach {
            assertNotNull(config.copy(serverIp = it).validationError())
        }
        listOf("0", "65536", "abc", "").forEach { assertNotNull(config.copy(serverPort = it).validationError()) }
    }
    @Test fun invalidPaths() {
        listOf("api/call", "//evil", "/api?x=y", "/../call").forEach {
            assertNotNull(config.copy(apiPath = it).validationError())
        }
    }
    @Test fun directionAndMissingNumbers() {
        assertNull(incomingNumber(false, false, "01012345678"))
        assertEquals("PRIVATE", incomingNumber(true, true, null))
        assertEquals("UNKNOWN", incomingNumber(true, false, null))
        assertEquals("UNKNOWN", incomingNumber(true, false, " "))
        assertEquals("+821012345678", incomingNumber(true, false, "+821012345678"))
    }
    @Test fun requestKeepsIdentityAndDetectionTime() {
        val event = CallEvent(phoneNumber = "UNKNOWN")
        UUID.fromString(event.eventId)
        OffsetDateTime.parse(event.receivedAt)
        val first = CallEventRequest.from(event, "2026-09-26T10:00:00+09:00")
        val second = CallEventRequest.from(event, "2026-09-26T10:01:00+09:00")
        assertEquals(first.eventId, second.eventId)
        assertEquals(first.receivedAt, second.receivedAt)
        assertNotEquals(first.sentAt, second.sentAt)
    }
    @Test fun retryClassification() {
        listOf(408, 429, 500, 503).forEach { assertTrue(httpFailure(it).retryable) }
        listOf(400, 401, 403, 404, 302).forEach { assertFalse(httpFailure(it).retryable) }
        assertTrue(networkFailure(SocketTimeoutException()).retryable)
    }
}
