# mobileCallReceive

Android 10 이상 스마트폰에서 수신 전화 정보를 같은 LAN의 Windows 앱으로 한 번 전송하는 Compose 앱입니다. `applicationId=net.onebell.mcs`와 패키지를 유지하므로 기존 설치 위에 업데이트됩니다.

## 사용

1. Android Studio에서 이 폴더를 열어 debug 빌드를 설치합니다.
2. [Windows 앱](../CallReceiver/README.md)을 실행합니다.
3. Android에 PC의 LAN IPv4, 포트 `18080`, 경로 `/api/call`을 저장합니다. `127.0.0.1`은 휴대폰 자신이므로 실제 스마트폰에서 PC 접속 주소로 사용할 수 없습니다.
4. PC 연결 테스트와 테스트 이벤트 전송을 확인한 뒤 시작을 누르고 Call Screening 역할을 허용합니다.

## 전송 정책

이벤트 JSON은 `schemaVersion`, `eventId`, `phoneNumber`, `receivedAt`, `sentAt`, `isTest`만 포함합니다. device ID, API key와 인증 헤더는 사용하지 않습니다.

수신 전화는 즉시 허용하고 HTTP를 한 번만 시도합니다. 연결 실패, 타임아웃, HTTP 오류나 프로세스 종료가 발생하면 이벤트를 저장하거나 재전송하지 않고 폐기합니다. 테스트 이벤트도 같은 정책을 사용합니다. 업데이트 후 최초 실행에는 과거 WorkManager 작업과 `call-events.db`를 정리합니다.

```powershell
.\gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:lintDebug
```

APK는 `app\build\outputs\apk\debug\app-debug.apk`에 생성됩니다. 실전화, 화면 OFF, 제조사 절전 정책과 LAN 방화벽은 실제 장비에서 별도로 확인해야 합니다.

설치용 서명 APK는 버전 태그로 자동 생성합니다. 배포 키와 GitHub Secrets 등록, 버전 변경 및 태그 생성 방법은 [GitHub Release 배포 안내](../docs/RELEASE.md)를 따릅니다.
