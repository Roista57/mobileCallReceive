package net.onebell.mcs

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import net.onebell.mcs.model.AppConfig
import org.junit.*
import org.junit.Assert.*

class SettingsUiTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()

    @Test fun editingRequiresSaveAndPersistsAcrossActivityRecreation() {
        compose.waitUntil(10_000) {
            compose.onAllNodes(hasText("서버 IP") and hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
        }
        compose.onNode(hasText("서버 IP") and hasSetTextAction()).performScrollTo().performTextReplacement("192.168.0.100")
        compose.onNodeWithText("저장하지 않은 변경 사항이 있습니다.").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("PC 연결 테스트").performScrollTo().performClick()
        compose.onNodeWithText("저장", useUnmergedTree = true).performScrollTo().performClick()
        compose.waitForIdle()
        compose.activityRule.scenario.recreate()
        compose.onNode(hasText("서버 IP") and hasSetTextAction()).performScrollTo().assertTextContains("192.168.0.100")
        val app = compose.activity.application as McsApplication
        runBlocking { assertEquals("192.168.0.100", app.settings.current().serverIp) }
    }
}
