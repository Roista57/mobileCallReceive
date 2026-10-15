package net.onebell.mcs.ui.settings

import android.content.ClipData
import android.content.ClipboardManager
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import net.onebell.mcs.R
import androidx.compose.ui.unit.dp
import net.onebell.mcs.data.*

@Composable
fun DeliveryLogScreen(state: SettingsState) {
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    var snapshot by remember { mutableStateOf<DeliveryLog?>(null) }
    val live = state.logs.firstOrNull { it.logId == selectedId }
    LaunchedEffect(live) { if (live != null) snapshot = live }
    val selected = live ?: snapshot
    if (selectedId != null && selected != null) {
        BackHandler { selectedId = null; snapshot = null }
        DeliveryLogDetail(selected) { selectedId = null; snapshot = null }
        return
    }
    Column(Modifier.fillMaxSize().padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("전송 로그", style = MaterialTheme.typography.headlineSmall)
        Text("최근 200건입니다. 항목을 누르면 전송 상세 정보를 확인할 수 있습니다.")
        if (state.logs.isEmpty()) Text("기록된 로그가 없습니다.")
        else LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(state.logs, key = { it.logId }) { log ->
                Card(onClick = { selectedId = log.logId; snapshot = log }, modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        Text(if (log.kind == "HEALTH") "${diagnosticTime(log.startedAt)} | PC 연결 테스트"
                            else "${diagnosticTime(log.receivedAt)} | ${log.phoneNumber}", style = MaterialTheme.typography.titleMedium)
                        Text("${log.method} ${log.destination}")
                        Text("${log.stateText}: ${log.result}", color = if (log.state == "FAILED")
                            MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface)
                        if (log.isTest) Text("테스트 이벤트", style = MaterialTheme.typography.labelMedium)
                    }
                }
            }
        }
    }
}
@Composable
private fun DeliveryLogDetail(log: DeliveryLog, close: () -> Unit) {
    val context = LocalContext.current
    var feedback by remember(log.logId) { mutableStateOf("") }
    val sections = remember(log) { deliveryLogSections(log) }
    val text = remember(log) { deliveryLogText(log) }
    val copied = stringResource(R.string.log_copy_success)
    val failed = stringResource(R.string.log_copy_failure)
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)) {
        TextButton(onClick = close) { Text("뒤로") }
        Text("전송 상세", style = MaterialTheme.typography.headlineSmall)
        SelectionContainer {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                sections.forEach { section -> Text(section) }
            }
        }
        Button(onClick = {
            feedback = try {
                context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("전송 상세", text))
                copied
            } catch (_: Exception) { failed }
        }, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.log_copy)) }
        if (feedback.isNotEmpty()) Text(feedback)
    }
}
