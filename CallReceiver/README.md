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

방화벽은 앱이 변경하지 않습니다. LAN 수신이 필요하면 개인 네트워크의 LocalSubnet만 허용하는 규칙을 직접 추가합니다.
