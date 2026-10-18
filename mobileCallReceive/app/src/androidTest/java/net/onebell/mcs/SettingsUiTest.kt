package net.onebell.mcs

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.flow.first
import net.onebell.mcs.model.AppConfig
import com.google.gson.JsonParser
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import java.util.concurrent.TimeUnit
import org.junit.*
import org.junit.Assert.*

class SettingsUiTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()
    private lateinit var saved: AppConfig
    @Before fun rememberSettings() = runBlocking {
        saved = (compose.activity.application as McsApplication).settings.current()
    }
    @After fun restoreSettings() = runBlocking {
        val settings = (compose.activity.application as McsApplication).settings
        settings.save(saved)
        settings.setEnabled(saved.enabled)
    }

    @Test fun deliveryLogOpensDetailsAndBackReturnsToList() {
        val app = compose.activity.application as McsApplication
        val id = java.util.UUID.randomUUID().toString()
        runBlocking {
            app.settings.upsertLog(net.onebell.mcs.data.DeliveryLog(logId = id, eventId = id,
                receivedAt = "2026-10-16T14:30:00+09:00", phoneNumber = "01012345678", state = "SUCCESS", success = true,
                destination = "http://192.168.0.50:18080/api/call", result = "PC 접수 확인 성공",
                attempts = listOf(net.onebell.mcs.data.DeliveryAttempt(number = 1, localIp = "192.168.0.20",
                    remoteIp = "192.168.0.50", remotePort = 18080, sentAt = "2026-10-16T00:01:02.123Z",
                    requestJson = "{\"phoneNumber\":\"01012345678\"}"))))
        }
        compose.onNodeWithText("로그").performClick()
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("01012345678", substring = true)).fetchSemanticsNodes().isNotEmpty() }
        compose.onAllNodes(hasText("01012345678", substring = true)).onFirst().performClick()
        compose.onNodeWithText("전송 상세").assertIsDisplayed()
        compose.onNodeWithText("192.168.0.20 → 192.168.0.50:18080").performScrollTo().assertIsDisplayed()
        compose.onNode(hasText("sentAt: 2026.10.16 09:01:02.123", substring = true)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("복사하기").performScrollTo().performClick()
        compose.onNodeWithText("전문을 복사했습니다.").performScrollTo().assertIsDisplayed()
        compose.runOnIdle {
            val clipboard = compose.activity.getSystemService(android.content.ClipboardManager::class.java)
            val copied = clipboard.primaryClip!!.getItemAt(0).text.toString()
            assertTrue(copied.contains(id))
            assertTrue(copied.contains("192.168.0.20 → 192.168.0.50:18080"))
            assertTrue(copied.contains("sentAt: 2026.10.16 09:01:02.123"))
            assertFalse(copied.contains("로컬 Port"))
            assertFalse(copied.contains("참고 주소"))
            val expected = runBlocking {
                net.onebell.mcs.data.deliveryLogText(app.settings.logs.first().single { it.logId == id })
            }
            assertEquals(expected, copied)
        }
        compose.onNodeWithText("뒤로").performScrollTo().performClick()
        compose.onNodeWithText("전송 로그").assertIsDisplayed()
    }

    @Test fun editingRequiresSaveAndPersistsAcrossActivityRecreation() {
        val testIp = if (saved.serverIp == "192.168.0.100") "192.168.0.101" else "192.168.0.100"
        compose.waitUntil(10_000) {
            compose.onAllNodes(hasText("서버 IP") and hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
        }
        compose.onNode(hasText("서버 IP") and hasSetTextAction()).performScrollTo().performTextReplacement(testIp)
        compose.onNodeWithText("저장하지 않은 변경 사항이 있습니다.").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("PC 연결 테스트").assertDoesNotExist()
        compose.onNodeWithText("테스트 전화 이벤트 전송").performScrollTo().performClick()
        compose.onNodeWithText("변경한 설정을 먼저 저장하세요.").assertIsDisplayed()
        compose.onNodeWithText("저장", useUnmergedTree = true).performScrollTo().performClick()
        compose.waitForIdle()
        compose.activityRule.scenario.recreate()
        compose.onNode(hasText("서버 IP") and hasSetTextAction()).performScrollTo().assertTextContains(testIp)
        val app = compose.activity.application as McsApplication
        runBlocking { assertEquals(testIp, app.settings.current().serverIp) }
    }

    @Test fun invalidInputAndRepeatedActionsShowTransientFeedback() {
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("서버 IP") and hasSetTextAction()).fetchSemanticsNodes().isNotEmpty() }
        compose.onNode(hasText("서버 IP") and hasSetTextAction()).performScrollTo().performTextReplacement("invalid")
        compose.onNodeWithText("저장", useUnmergedTree = true).performScrollTo().performClick()
        compose.onNodeWithText("올바른 IPv4 주소를 입력하세요.").assertIsDisplayed()
        compose.onNode(hasText("서버 IP") and hasSetTextAction()).performScrollTo().performTextReplacement(saved.serverIp)
        compose.onNodeWithText("저장", useUnmergedTree = true).performScrollTo().performClick()
        compose.waitUntil(10_000) { compose.onAllNodes(hasText("설정을 저장했습니다.")).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("설정을 저장했습니다.").assertIsDisplayed()
    }

    @Test fun testEventAlsoUpdatesConnectionStatusWithoutHealthRequest() {
        val app = compose.activity.application as McsApplication
        if (android.os.Build.VERSION.SDK_INT >= 37) {
            val descriptor = InstrumentationRegistry.getInstrumentation().uiAutomation.executeShellCommand(
                "pm grant net.onebell.mcs android.permission.ACCESS_LOCAL_NETWORK"
            )
            android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).bufferedReader().use { it.readText() }
        }
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    if (request.method != "POST" || request.path != "/custom/call")
                        return MockResponse().setResponseCode(404)
                    val json = JsonParser.parseString(request.body.clone().readUtf8()).asJsonObject
                    return MockResponse().setBody("""{"success":true,"eventId":${json["eventId"]}}""")
                }
            }
            server.start()
            runBlocking {
                app.settings.save(AppConfig(serverIp = "127.0.0.1", serverPort = server.port.toString(),
                    apiPath = "/custom/call", enabled = false))
            }
            compose.waitUntil(10_000) {
                compose.onAllNodes(hasText("/custom/call") and hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
            }
            compose.onNodeWithText("PC 연결 테스트").assertDoesNotExist()
            compose.onNodeWithText("테스트 전화 이벤트 전송").performScrollTo().performClick()
            compose.waitUntil(10_000) {
                compose.onAllNodes(hasText("PC 연결: 성공 ·", substring = true)).fetchSemanticsNodes().isNotEmpty()
            }
            assertEquals(1, server.requestCount)
            val request = server.takeRequest(1, TimeUnit.SECONDS)!!
            assertEquals("POST", request.method)
            assertEquals("/custom/call", request.path)
            val json = JsonParser.parseString(request.body.readUtf8()).asJsonObject
            assertTrue(json["isTest"].asBoolean)
            assertFalse(runBlocking { app.settings.current().enabled })
            val log = runBlocking { app.settings.logs.first().single { it.eventId == json["eventId"].asString } }
            assertTrue(log.success)
            assertEquals("CALL", log.kind)
            assertEquals(1, log.attempts.size)

            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest) = MockResponse().setResponseCode(503)
            }
            compose.waitForIdle()
            compose.onNodeWithText("테스트 전화 이벤트 전송").performScrollTo().performClick()
            compose.waitUntil(10_000) {
                compose.onAllNodes(hasText("PC 연결: 실패")).fetchSemanticsNodes().isNotEmpty()
            }
            assertEquals(2, server.requestCount)
            assertEquals("POST", server.takeRequest(1, TimeUnit.SECONDS)!!.method)
        }
    }
}
