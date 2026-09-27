package net.onebell.mcs.ui.settings

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun DeliveryLogScreen(state: SettingsState) {
    Column(Modifier.fillMaxSize().padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("전송 로그", style = MaterialTheme.typography.headlineSmall)
        Text("최근 200건의 전화 수신 및 테스트 전송 결과입니다.")
        if (state.logs.isEmpty()) {
            Text("기록된 로그가 없습니다.")
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(state.logs) { log ->
                    Card(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                            Text("${localTime(log.receivedAt)} | ${log.phoneNumber}",
                                style = MaterialTheme.typography.titleMedium)
                            Text("${log.method} ${log.destination}")
                            Text(if (log.success) "성공: ${log.result}" else "실패: ${log.result}",
                                color = if (log.success) MaterialTheme.colorScheme.onSurface
                                else MaterialTheme.colorScheme.error)
                            if (log.isTest) Text("테스트 이벤트", style = MaterialTheme.typography.labelMedium)
                        }
                    }
                }
            }
        }
    }
}

private fun localTime(value: String): String = try {
    OffsetDateTime.parse(value).atZoneSameInstant(ZoneId.systemDefault())
        .format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss"))
} catch (_: Exception) { value }
