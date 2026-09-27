# mobileCallReceive

Android 수신 전화 정보를 같은 LAN의 Windows PC에 전달하고 WPF 팝업으로 표시하는 프로젝트입니다.

- [`mobileCallReceive`](mobileCallReceive/README.md): Android 10 이상 Compose 앱
- [`CallReceiver`](CallReceiver/README.md): .NET 10 Windows 수신 앱
- [GitHub Release 배포 안내](docs/RELEASE.md): 배포 키, Secrets, 버전, 태그와 설치 방법

`main` push와 main 대상 pull request에서는 변경 경로에 따라 필요한 플랫폼의 CI만 실행합니다. `version.properties`와 일치하는 `v1.0.0` 형식의 태그를 push하면 두 플랫폼을 모두 빌드·검증하고 서명된 APK, Windows x64 런타임 포함 ZIP, SHA-256 체크섬을 GitHub Release로 게시합니다.

## CI 실행 기준

| 변경 경로 | 실행할 빌드 |
|---|---|
| `mobileCallReceive/**` | Android |
| `CallReceiver/**` | Windows |
| `version.properties`, `.github/**`, `.gitattributes` | 둘 다 |
| 루트 `Directory.Build.props`, `Directory.Build.targets`, `global.json`, `NuGet.Config` | Windows |
| Markdown 파일, 루트·각 플랫폼의 `docs/**`, 루트 `.gitignore` | 생략 |
| 분류되지 않은 파일 | 둘 다 |

문서는 플랫폼 폴더 안에 있어도 제외하지만 `.github/**`는 문서까지 두 플랫폼을 검증합니다. 여러 경로 변경은 필요한 빌드를 합칩니다. 삭제와 이동도 감지하며, push는 이전 커밋부터, PR은 공통 조상부터 누적 변경을 확인합니다. 비교 기준 커밋이 없으면 두 플랫폼을 실행하고 분석 오류는 실패로 처리합니다.

문서만 변경해도 `Detect changes`와 `CI result`는 실행됩니다. Actions 작업 요약에서 플랫폼별 실행·생략 이유와 결과를 확인할 수 있습니다. 브랜치 보호의 필수 검사를 사용하는 경우 `CI result`를 지정하세요. 필요한 빌드의 실패·취소·예상치 않은 생략은 최종 검사 실패로 처리됩니다. Release에는 경로 필터를 적용하지 않습니다.

변경 감지와 결과 판정 테스트는 다음 명령으로 실행합니다. 임시 Git 저장소만 사용합니다.

```powershell
python -B -m unittest discover -s .github/scripts -p 'test_ci.py' -v
```
