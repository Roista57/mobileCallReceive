# 구현 구조

## Android

프로젝트명과 앱 표시명은 `mobileCallReceive`이며 기존 설치 호환을 위해 `applicationId=net.onebell.mcs`와 Kotlin 패키지는 유지합니다. 설정은 DataStore에 IP, Port, API 경로, 활성 상태와 마지막 결과만 저장합니다. 이전 deviceId와 apiKey 키는 최초 실행 마이그레이션에서 삭제합니다.

CallScreeningService는 수신 전화를 즉시 허용한 뒤 애플리케이션 Coroutine에서 HTTP를 한 번 호출합니다. 이벤트는 메모리에서만 유지되며 실패 시 마지막 오류를 남기고 폐기합니다. 과거 WorkManager 작업과 `call-events.db`는 마이그레이션에서 한 번 정리합니다.

## Windows

WPF 앱은 Kestrel, SQLite 이벤트 저장소, 팝업 표시 관리자와 트레이를 한 프로세스에서 실행합니다. API는 인증 헤더 없이 `/api/health`와 `/api/call`을 제공합니다. eventId UNIQUE 제약으로 서버 측 중복 팝업을 막습니다. 서버는 사용자가 입력한 IPv4와 포트에만 바인딩됩니다.

화면은 WPF 기본 컨트롤과 시스템 색상을 사용합니다. 서버 상태 영역은 실행 여부, 실제 주소, 포트와 마지막 오류를 표시합니다. 알림 시간은 현지 시간으로 바꾼 뒤 사용자가 저장한 .NET 형식으로 출력합니다.

실행 파일 옆 `config-location.json`이 설정 디렉터리를 지정합니다. 지정이 없으면 실행 파일 옆의 `settings.json`과 `events.sqlite3`을 사용합니다. 위치 변경은 대상 충돌을 검사하고 파일 복사가 끝난 뒤 bootstrap을 원자적으로 교체하며 다음 실행부터 적용합니다.
