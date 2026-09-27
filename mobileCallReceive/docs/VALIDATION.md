# 검증 기록

## 자동 검증

- Android: 단위 테스트, debug APK 조립, androidTest Kotlin 컴파일, lint
- Windows: .NET 테스트 23개(설정, API, SQLite 중복 제거, 팝업 큐, 기본 UI, 설정 위치, 구형 payload 호환)
- Windows x64 self-contained publish와 배포본 smoke test

## 장비 검증이 필요한 항목

실제 스마트폰의 수신 전화, 연락처 등록/미등록 번호, 표시 제한 전화, 화면 OFF와 제조사 절전 정책은 미검증입니다. PC LAN 방화벽과 공유기 AP 격리, 듀얼 모니터 혼합 DPI도 실제 환경에서 확인해야 합니다.

실기기에서는 Android에 `127.0.0.1` 대신 PC LAN IPv4를 입력합니다. 서버 중지 상태에서 발생한 전화는 한 번 실패한 뒤 폐기되며, 서버를 다시 시작해도 과거 이벤트가 나타나지 않아야 합니다.
