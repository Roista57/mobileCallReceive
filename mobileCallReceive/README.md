# mobileCallReceive

Android 10 이상 스마트폰에서 수신 전화 정보를 같은 LAN의 Windows 앱으로 전달하는 Compose 앱입니다. `applicationId=net.onebell.mcs`와 패키지를 유지하므로 기존 설치 위에 업데이트됩니다.

## 사용

1. Android Studio에서 이 폴더를 열어 debug 빌드를 설치합니다.
2. [Windows 앱](../CallReceiver/README.md)을 실행합니다.
3. Android에 PC의 LAN IPv4, 포트 `18080`, 경로 `/api/call`을 저장합니다. `127.0.0.1`은 휴대폰 자신이므로 실제 스마트폰에서 PC 접속 주소로 사용할 수 없습니다.
4. PC 연결 테스트와 테스트 이벤트 전송을 확인한 뒤 시작을 누르고 Call Screening 역할을 허용합니다.

## 전송 정책

이벤트 JSON은 `schemaVersion`, `eventId`, `phoneNumber`, `receivedAt`, `sentAt`, `isTest`만 포함합니다. device ID, API key와 인증 헤더는 사용하지 않습니다.

수신 전화는 즉시 허용합니다. 연결 실패·타임아웃·소켓 연결 끊김은 최초 요청에 더해 최대 1회 즉시 재요청합니다. connect/read/write/call timeout은 각각 5초이며, 전체 요청 한 번은 최대 5초로 제한됩니다. HTTP 오류(408·429·5xx 포함), 잘못된 JSON·success/eventId 불일치·주소/권한/TLS 오류는 재요청하지 않습니다. 테스트 전화와 PC 연결 테스트도 같은 정책을 사용합니다.

재요청은 같은 eventId·receivedAt을 유지하며 sentAt만 갱신합니다. 실제 전화는 재요청 전에 서비스 사용 상태를 다시 확인하므로 중지하면 다음 시도를 보내지 않습니다. 이미 서버에 도착한 요청은 취소할 수 없습니다. 최종 실패나 프로세스 종료 후 이벤트를 전송 큐에 보관하거나 재시작 시 보내지 않습니다. 기존 WorkManager 작업과 `call-events.db` 정리는 유지합니다.

## 상세 전송 로그

로그 탭의 카드를 누르면 수신 시각·번호, 대상 URL, 요청 JSON, 응답 상태·본문, 시도별 결과·처리 단계와 원본 오류를 확인할 수 있습니다. `휴대폰 IP → 서버 IP:Port`는 실제 연결 소켓에서 확인한 주소입니다. 연결 전에 실패하면 IP 미확인으로 표시합니다. 로컬 Port와 참고 주소는 수집·표시하지 않습니다. 화면의 시각과 별도 `sentAt` 항목은 한국 시간 `yyyy.MM.dd HH:mm:ss.SSS`로 표시하며 요청 JSON 자체는 실제 전송한 원문을 유지합니다.

상세 화면 마지막의 `복사하기`를 누르면 모든 전송 시도·JSON·오류·잘림 안내를 포함한 전문을 일반 텍스트로 복사합니다. 화면과 복사는 같은 포맷을 사용하며 성공·실패 안내를 표시합니다.

최근 200건을 DataStore에 보관하며 한 이벤트의 두 시도는 같은 로그에 묶습니다. 구형 로그는 기존 요약을 유지하고 상세가 없는 항목을 기록 없음으로 표시합니다. 프로세스가 종료되어 미완료로 남은 로그는 완료 여부 확인 불가로 표시하며 재전송하지 않습니다. 요청 진단은 최대 16KB, 응답 진단은 최대 4KB까지만 저장하고 잘림·불완전 읽기를 표시합니다.

`PC 접수 확인 성공`은 PC의 success/eventId 응답 확인을 뜻하며 팝업 표시를 보장하지 않습니다. Timeout이어도 PC가 이미 이벤트를 저장했을 수 있습니다. PC 상세 창에서 같은 eventId의 저장·중복 여부와 팝업 상태를 대조하세요. HTTP 서버까지 도달하지 않은 실패는 PC 최근 요청에 나타나지 않을 수 있습니다.

```powershell
.\gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:lintDebug
```

APK는 `app\build\outputs\apk\debug\app-debug.apk`에 생성됩니다. 실전화, 화면 OFF, 제조사 절전 정책과 LAN 방화벽은 실제 장비에서 별도로 확인해야 합니다.

설치용 서명 APK는 버전 태그로 자동 생성합니다. 배포 키와 GitHub Secrets 등록, 버전 변경 및 태그 생성 방법은 [GitHub Release 배포 안내](../docs/RELEASE.md)를 따릅니다.
