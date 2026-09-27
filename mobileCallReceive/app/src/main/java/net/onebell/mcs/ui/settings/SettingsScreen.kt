package net.onebell.mcs.ui.settings

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun SettingsScreen(state: SettingsState, viewModel: SettingsViewModel,
    onAction: (SettingsAction) -> Unit, onPermissions: () -> Unit) {
    val config = state.draft
    val usable = state.loaded && !state.busy
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                Text("서비스 사용: ${if (state.saved.enabled) "ON" else "OFF"}")
                Text("Call Screening 역할: ${if (state.role) "허용됨" else "없음"}")
                Text("연락처 권한: ${if (state.contacts) "허용됨" else "없음 · 등록 번호 감지 제한"}")
                Text("PC 연결: ${state.health}")
                Text("마지막 전송: ${koreaTime(state.status.lastSent)}")
                if (state.status.lastWasTest && state.status.lastSent.isNotEmpty()) Text("테스트 이벤트 전송 성공")
            }
        }
        OutlinedTextField(config.serverIp, { viewModel.edit(config.copy(serverIp = it.trim())) },
            label = { Text("서버 IP") }, singleLine = true, enabled = usable, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(config.serverPort, { viewModel.edit(config.copy(serverPort = it)) },
            label = { Text("Port") }, singleLine = true, enabled = usable, modifier = Modifier.fillMaxWidth(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
        OutlinedTextField(config.apiPath, { viewModel.edit(config.copy(apiPath = it.trim())) },
            label = { Text("API Path") }, singleLine = true, enabled = usable, modifier = Modifier.fillMaxWidth())
        if (state.dirty) Text("저장하지 않은 변경 사항이 있습니다.", color = MaterialTheme.colorScheme.error)
        Button(viewModel::save, enabled = usable, modifier = Modifier.fillMaxWidth()) { Text("저장") }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button({ onAction(SettingsAction.START) }, enabled = usable, modifier = Modifier.weight(1f)) { Text("시작") }
            OutlinedButton(viewModel::stop, enabled = usable, modifier = Modifier.weight(1f)) { Text("중지") }
        }
        OutlinedButton({ onAction(SettingsAction.HEALTH) }, enabled = usable, modifier = Modifier.fillMaxWidth()) { Text("PC 연결 테스트") }
        OutlinedButton({ onAction(SettingsAction.TEST) }, enabled = usable, modifier = Modifier.fillMaxWidth()) { Text("테스트 전화 이벤트 전송") }
        if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        TextButton(onPermissions) { Text("앱 권한 설정 열기") }
    }
}

private fun koreaTime(value: String): String = if (value.isEmpty()) "없음" else try {
    OffsetDateTime.parse(value).atZoneSameInstant(ZoneId.of("Asia/Seoul"))
        .format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss"))
} catch (_: Exception) { value }
