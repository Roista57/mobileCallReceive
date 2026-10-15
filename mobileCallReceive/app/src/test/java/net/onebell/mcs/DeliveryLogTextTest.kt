package net.onebell.mcs

import com.google.gson.Gson
import net.onebell.mcs.data.*
import org.junit.Assert.*
import org.junit.Test

class DeliveryLogTextTest {
    @Test fun screenAndCopyShareEveryAttemptAndKeepRawJson() {
        val raw = """{"sentAt":"2026-10-16T00:01:02.123Z","phoneNumber":"01012345678"}"""
        val log = DeliveryLog(logId = "sample", eventId = "event", phoneNumber = "01012345678", state = "SUCCESS",
            attempts = listOf(DeliveryAttempt(number = 1, sentAt = "2026-10-16T00:01:02.123Z",
                localIp = "192.168.0.20", remoteIp = "192.168.0.50", remotePort = 18080,
                requestJson = raw, category = "TIMEOUT", errorMessage = "timeout", retryPlanned = true,
                responseIncomplete = true, responseTruncated = true, requestTruncated = true),
                DeliveryAttempt(number = 2, sentAt = "2026-10-16T00:01:03Z", result = "PC 접수 확인 성공", success = true)))
        val text = deliveryLogText(log)
        assertEquals((listOf("전송 상세") + deliveryLogSections(log)).joinToString("\n\n"), text)
        assertTrue(text.contains("1차 시도")); assertTrue(text.contains("2차 시도"))
        assertTrue(text.contains("192.168.0.20 → 192.168.0.50:18080"))
        assertTrue(text.contains("sentAt: 2026.10.16 09:01:02.123"))
        assertTrue(text.contains(raw)); assertTrue(text.contains("4KB")); assertTrue(text.contains("16KB"))
        assertTrue(text.contains("timeout")); assertTrue(text.contains("완료되지 않았습니다"))
        assertFalse(text.contains("로컬 Port")); assertFalse(text.contains("참고 주소"))
    }
    @Test fun removedLegacyFieldsAreIgnoredAndNotSavedAgain() {
        val legacy = """[{"logId":"legacy","attempts":[{"localPort":50123,"network":{"transport":"Wi-Fi","referenceAddresses":["10.0.0.20"]},"sentAt":"2026-10-16T00:00:00Z"}]}]"""
        val log = DeliveryLogCodec.decode(legacy).single()
        val json = Gson().toJson(log)
        assertFalse(json.contains("localPort")); assertFalse(json.contains("referenceAddresses"))
        val text = deliveryLogText(log)
        assertTrue(text.contains("Wi-Fi")); assertTrue(text.contains("2026.10.16 09:00:00.000"))
        assertFalse(text.contains("50123")); assertFalse(text.contains("10.0.0.20"))
    }
    @Test fun legacySummaryAndHealthWithoutTimestampCanBeCopied() {
        val legacy = DeliveryLog(phoneNumber = "PRIVATE", result = "이전 로그", success = true)
        assertTrue(deliveryLogText(legacy).contains("기록 없음"))
        assertTrue(deliveryLogText(legacy).contains("PRIVATE"))
        val health = DeliveryLog(kind = "HEALTH", attempts = listOf(DeliveryAttempt()))
        assertTrue(deliveryLogText(health).contains("sentAt: 해당 없음"))
    }
}
