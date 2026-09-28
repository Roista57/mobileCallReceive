# Windows 전화 수신 앱

.NET 10 WPF 앱이 Android의 전화 이벤트를 받아 SQLite에 기록하고 순서대로 팝업을 표시합니다.

## 실행

```powershell
cd F:\000_favorite\08.Git\mobileCallReceive\CallReceiver
dotnet run --project CallReceiver
```

기본 주소는 `127.0.0.1:18080`입니다. 실제 스마트폰을 연결하려면 PC의 LAN IPv4를 직접 입력하고 Android에도 같은 주소와 포트를 입력합니다. 서버는 입력한 IPv4 하나에만 바인딩됩니다.

화면 상단에서 서버 실행 상태, 실제 주소, 포트와 마지막 오류를 확인할 수 있습니다. HTTP health 테스트와 알림 위치 테스트를 제공하며 가짜 전화 HTTP 테스트는 제거했습니다.

## 설정과 데이터

기본적으로 실행 파일 옆에 `settings.json`과 `events.sqlite3`를 만듭니다. `config-location.json`은 사용자가 고른 설정 디렉터리를 기록합니다. 프로그램 설정 탭에서 빈 폴더를 선택하면 현재 설정과 DB를 복사하며 다음 실행부터 적용합니다. `--data-dir <path>`는 bootstrap보다 우선합니다.

알림 시간 형식은 프리셋을 선택하거나 .NET 형식을 직접 입력할 수 있습니다. 기본값은 `yyyy-MM-dd HH:mm:ss`입니다.

## 테스트와 배포

```powershell
dotnet test CallReceiver.slnx
dotnet publish CallReceiver\CallReceiver.csproj -c Release -r win-x64 --self-contained true -o artifacts\win-x64
```

배포 실행 파일은 `artifacts\win-x64\CallReceiver.exe`입니다.

언어별 리소스는 프로젝트의 `SatelliteResourceLanguages=ko;en` 설정으로 한국어와 영어만 포함합니다. 영어 기본 리소스는 본체 DLL에 포함될 수 있으므로 별도의 `en` 폴더가 없어도 정상입니다. 앱 화면 번역이나 언어 선택 기능을 추가하는 설정은 아닙니다.

로컬 publish 출력은 새 빈 폴더를 사용하세요. 기존 폴더에 다시 publish하거나 새 ZIP을 덮어 풀면 이전 언어 폴더가 남을 수 있습니다. Release는 매번 새 출력 폴더를 만들고 한국어 리소스 존재 및 다른 언어 리소스 부재를 검사한 뒤 ZIP을 생성합니다. 새 ZIP도 빈 폴더에 압축 해제하세요. 기존 사용자 설정과 DB는 별도로 보존해야 합니다.

언어 제한 검증 결과(2026-09-28): Release 빌드와 비 UI 테스트 25개가 통과했습니다. 동일 소스를 각각 빈 폴더에 win-x64 self-contained publish한 결과, 전체 파일 크기는 209,748,455바이트에서 193,194,167바이트로, ZIP은 87,141,124바이트에서 81,430,047바이트로 감소했습니다. ZIP의 한국어 리소스 17개와 실행 파일·본체 DLL·필수 런타임을 확인했으며 다른 언어 리소스는 없었습니다. 영어는 본체의 기본 리소스를 사용합니다. 앱을 실행하지 않았으므로 한국어·영어 Windows의 실제 UI 동작 및 GitHub Release 실행 결과는 미검증입니다. 용량은 SDK 및 의존성 버전에 따라 달라집니다.

정식 배포본은 버전 태그로 자동 생성합니다. 배포 키와 GitHub Secrets 등록, 버전 변경 및 태그 생성 방법은 [GitHub Release 배포 안내](../docs/RELEASE.md)를 따릅니다.

방화벽은 앱이 변경하지 않습니다. LAN 수신이 필요하면 개인 네트워크의 LocalSubnet만 허용하는 규칙을 직접 추가합니다.
