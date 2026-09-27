# GitHub Release 배포 안내

`v1.0.0`처럼 공통 버전과 일치하는 태그를 push하면 GitHub Actions가 Android와 Windows 산출물을 검증하고 Release를 공개합니다. 로컬 앱 실행이나 수동 산출물 업로드는 필요하지 않습니다.

## 최초 1회: Android 배포 키 만들기

배포 키는 앱 업데이트의 신원을 결정합니다. 같은 `applicationId`의 다음 APK도 반드시 같은 키로 서명해야 하므로, keystore와 암호를 별도의 안전한 장소에 백업합니다. 저장소, Release ZIP, 메신저에는 키를 넣지 않습니다.

PowerShell에서 다음 예시처럼 키를 생성합니다. 별칭과 암호는 원하는 값으로 정할 수 있습니다.

```powershell
keytool -genkeypair -v `
  -keystore mobileCallReceive-release.jks `
  -alias mobileCallReceive `
  -keyalg RSA `
  -keysize 4096 `
  -validity 10000
```

keystore를 GitHub Secret에 넣을 Base64 문자열로 변환합니다.

```powershell
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path .\mobileCallReceive-release.jks))
[Convert]::ToBase64String($bytes) | Set-Clipboard
```

GitHub 저장소의 **Settings → Secrets and variables → Actions → New repository secret**에서 다음 네 값을 등록합니다.

| Secret | 값 |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | 위 명령으로 만든 Base64 전체 문자열 |
| `ANDROID_KEYSTORE_PASSWORD` | keystore 암호 |
| `ANDROID_KEY_ALIAS` | 키 별칭(예: `mobileCallReceive`) |
| `ANDROID_KEY_PASSWORD` | 키 암호 |

Secret이 하나라도 없거나 keystore와 암호가 맞지 않으면 release APK 생성이 실패하며 unsigned APK나 debug 키로 대체하지 않습니다.

기존에 설치한 debug APK는 배포 키가 다르므로 최초 배포 APK 설치 전에 삭제해야 할 수 있습니다. 앱을 삭제하면 해당 앱의 로컬 설정도 삭제됩니다. 이후에는 같은 배포 키와 증가한 `versionCode`를 사용한 APK로 업데이트할 수 있습니다.

## 버전 올리기와 배포하기

루트 [`version.properties`](../version.properties)에서 두 값을 함께 관리합니다.

```properties
versionName=1.0.0
versionCode=2
```

- `versionName`은 Android `versionName`, Windows 제품 버전, Git 태그 이름에 사용합니다.
- `versionCode`는 Android 업데이트마다 이전 값보다 큰 양의 정수로 올립니다.
- 태그는 반드시 `v` + `versionName`이어야 합니다. 값이 다르면 배포가 중단됩니다.

변경 사항을 먼저 `main`에 push하고 CI 성공을 확인한 다음 해당 커밋에 태그를 만듭니다.

```powershell
git add .
git diff --cached --stat
git commit -m "ci(release): Android APK 및 Windows 실행 파일 자동 배포 구성"
git push origin main

git tag -a v1.0.0 -m "release: v1.0.0 배포"
git push origin v1.0.0
```

Release 작업은 다음 파일을 공개합니다.

```text
mobileCallReceive-v1.0.0.apk
CallReceiver-win-x64-v1.0.0.zip
SHA256SUMS.txt
```

Android 작업은 JDK 25와 Android SDK 37에서 테스트·lint를 실행하고 APK 서명, 패키지명 `net.onebell.mcs`, 버전을 확인합니다. Windows 작업은 .NET 10에서 화면을 띄우지 않는 테스트를 실행한 뒤 win-x64 런타임 포함 폴더를 압축합니다. ZIP에는 사용자 `settings.json`, 위치 bootstrap, 이벤트 DB, 테스트 결과, 서명 키를 포함하지 않습니다.

## 설치와 확인

Android에서는 APK를 내려받아 설치합니다. 알 수 없는 앱 설치 권한이 필요할 수 있습니다. 앱의 PC 주소에는 `127.0.0.1` 대신 같은 네트워크에 연결된 Windows PC의 LAN IPv4를 입력합니다. release APK도 이 LAN HTTP 연결을 허용하도록 구성되어 있습니다.

Windows에서는 ZIP을 원하는 쓰기 가능한 폴더에 완전히 압축 해제하고 `CallReceiver.exe`를 실행합니다. .NET 런타임은 ZIP 안에 포함됩니다. Windows 코드 서명은 적용하지 않았으므로 처음 실행할 때 Windows의 게시자 확인 화면이 표시될 수 있습니다.

`SHA256SUMS.txt`는 다운로드가 손상되지 않았는지 확인하는 데 사용합니다.

```powershell
Get-FileHash .\mobileCallReceive-v1.0.0.apk -Algorithm SHA256
Get-FileHash .\CallReceiver-win-x64-v1.0.0.zip -Algorithm SHA256
```

출력한 해시가 `SHA256SUMS.txt`와 같아야 합니다.

## 실패한 작업 다시 실행하기

Android API 37의 SDK 저장소 패키지 ID는 `platforms;android-37.0`입니다. 앱의 `compileSdk`와 `targetSdk`는 정수 `37`을 그대로 사용합니다. CI와 Release는 같은 SDK 패키지와 Build Tools `36.0.0`을 설치하고 `android.jar` 및 `apksigner`가 존재하는지 확인합니다. `Failed to find package 'platforms;android-37'` 오류는 서명 Secret 문제가 아니라 설치 패키지 이름 오류입니다.

워크플로 파일을 수정한 경우에는 변경을 커밋·push해 새 CI를 실행해야 합니다. 기존 실행의 재실행은 기존 커밋을 사용하므로 새 수정이 적용되지 않습니다. Release 태그도 수정된 커밋을 가리켜야 합니다. 이미 공개한 버전은 새 버전으로 배포하고, 아직 공개하지 않은 실패한 태그만 상태 확인 후 다시 생성합니다.

GitHub 저장소의 **Actions → Release → 실패한 실행 → Re-run failed jobs**를 선택합니다. 빌드가 실패하면 공개 Release를 만들지 않습니다. 파일 업로드 또는 첨부 확인 중 실패해 남은 draft Release는 같은 작업을 재실행할 때 새 draft로 다시 만들어집니다. 이미 공개된 같은 태그의 Release는 자동으로 덮어쓰지 않습니다.

태그와 버전이 다르면 잘못 만든 태그를 원격과 로컬에서 삭제한 뒤 올바른 커밋에 다시 만듭니다. 이미 공개된 Release가 있다면 자동화가 이를 변경하지 않으므로 GitHub에서 상태를 먼저 확인합니다.

## 수동 검증 범위

일반 CI에서는 `Category=Desktop`인 WPF 창·포커스·트레이 테스트를 제외합니다. Windows 데스크톱에서 압축을 푼 프로그램을 실행해 서버 시작, HTTP 접수, 팝업 위치와 포커스, 트레이 동작을 별도로 확인합니다. Android 실기기에서는 설치와 다음 버전 덮어쓰기, PC LAN health, 테스트 이벤트, 실제 수신 전화를 확인합니다. 이 장비 검증과 실제 GitHub Release 다운로드 검증은 워크플로 파일만 구현한 시점에는 미검증입니다.
