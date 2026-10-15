package net.onebell.mcs.data

/** The screen and clipboard share these exact sections, including original JSON payloads. */
fun deliveryLogSections(log: DeliveryLog): List<String> = buildList {
    add("결과: ${log.stateText} · ${log.result}")
    add("로그 ID: ${log.logId}\neventId: ${log.eventId ?: "기록 없음"}")
    add("종류: ${if (log.kind == "HEALTH") "PC 연결 테스트" else if (log.isTest) "테스트 전화" else "실제 전화"}")
    add("전화 수신: ${diagnosticTime(log.receivedAt)}\n전화번호: ${log.phoneNumber.ifBlank { "해당 없음" }}")
    add("시작: ${diagnosticTime(log.startedAt)}\n종료: ${diagnosticTime(log.finishedAt)}")
    add("${log.method} ${log.destination}\nContent-Type: ${if (log.kind == "HEALTH") "해당 없음" else "application/json"}")
    if (log.attempts.isEmpty()) add("시도별 상세 정보: 기록 없음")
    log.attempts.forEach { attempt ->
        add("${attempt.number}차 시도 · ${attempt.network.transport}")
        val remote = attempt.remoteIp?.let { "$it:${attempt.remotePort ?: "미확인"}" }
            ?: "설정 대상 ${log.destination.substringAfter("://").substringBefore('/')}"
        add("${attempt.localIp ?: "로컬 IP 미확인"} → $remote")
        add("시작: ${diagnosticTime(attempt.startedAt)}\n종료: ${diagnosticTime(attempt.finishedAt)}\n경과: ${attempt.elapsedMs}ms\nsentAt: ${attempt.sentAt?.let(::diagnosticTime) ?: "해당 없음"}")
        add("결과: ${attempt.result.ifBlank { "진행 중/기록 없음" }}\n단계: ${attempt.phase}\nHTTP: ${attempt.httpStatus ?: "응답 없음"}")
        if (attempt.retryPlanned) add("통신 오류로 최대 1회 재요청 예정 (중지 시 취소)")
        if (attempt.category != null) add("오류 분류: ${attempt.category}\n예외: ${attempt.errorType ?: "해당 없음"}\n메시지: ${attempt.errorMessage ?: "해당 없음"}\n원인: ${attempt.causes.joinToString("\n")}")
        add("처리 흐름: " + attempt.steps.joinToString(" → ") { "${it.stage} (+${it.elapsedMs}ms)" })
        add("요청 JSON\n${attempt.requestJson ?: "본문 없음/기록 없음"}")
        if (attempt.requestTruncated) add("요청 기록은 최대 16KB로 잘렸습니다.")
        add("응답 본문\n${attempt.responseText ?: "응답 없음/기록 없음"}")
        if (attempt.responseTruncated) add("응답 기록은 최대 4KB로 잘렸습니다.")
        if (attempt.responseIncomplete) add("응답 본문 읽기가 완료되지 않았습니다.")
        if (attempt.responseInvalidUtf8) add("응답에 UTF-8로 해석할 수 없는 바이트가 있습니다.")
    }
    add("접수 확인 성공은 팝업 표시 확인과 다릅니다. Timeout이어도 PC에서 이미 저장했을 수 있습니다.")
}
fun deliveryLogText(log: DeliveryLog): String = (listOf("전송 상세") + deliveryLogSections(log)).joinToString("\n\n")
