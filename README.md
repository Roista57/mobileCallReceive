# mobileCallReceive

Android 수신 전화 정보를 같은 LAN의 Windows PC에 전달하고 WPF 팝업으로 표시하는 프로젝트입니다.

- [`mobileCallReceive`](mobileCallReceive/README.md): Android 10 이상 Compose 앱
- [`CallReceiver`](CallReceiver/README.md): .NET 10 Windows 수신 앱
- [GitHub Release 배포 안내](docs/RELEASE.md): 배포 키, Secrets, 버전, 태그와 설치 방법

`main` push와 pull request에서는 두 플랫폼의 CI를 실행합니다. `version.properties`와 일치하는 `v1.0.0` 형식의 태그를 push하면 서명된 APK, Windows x64 런타임 포함 ZIP, SHA-256 체크섬을 GitHub Release로 게시합니다.
