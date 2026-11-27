# LIGClaw Fluent UI 현대화 인수 기준

> 상태: 구현 및 자동 검증 완료, Windows 10 22H2 실기 확인 대기
> 기준일: 2026-08-12

## 1. 기술 기준

- 데스크톱 UI는 `WPF-UI 4.3.0`을 고정해 사용한다.
- `ThemesDictionary`와 `ControlsDictionary`를 먼저 불러오고 LIGClaw 브랜드 토큰과 의미 스타일을 그 위에 적용한다.
- 모든 독립 창은 `FluentWindow`를 사용한다.
- 모든 `FluentWindow`는 최소화·최대화·닫기 제어가 있는 Wpf.Ui `TitleBar`를 정확히 한 개 포함한다.
- 기능 카드와 기능 만들기 단계는 Wpf.Ui `Card`, 상태는 `Badge`, 중요한 경고는 `InfoBar`, 이진 설정은 `ToggleSwitch`를 사용한다.
- Windows 11 전용 Mica, 시스템 `Segoe Fluent Icons` 글꼴 또는 강제 backdrop에 의존하지 않는다.
- `Wpf.Ui.dll`과 `ThirdPartyNotices.txt`는 자체 포함 설치 패키지에 함께 들어간다.

## 2. 화면 적용 목록

| 영역 | 화면 | 인수 기준 |
|---|---|---|
| 셸 | MainWindow | FluentWindow, 적응형 왼쪽 탐색, 명확한 주요 동작, 760×500에서 핵심 입력 유지 |
| 대화 | 대화 작성·타임라인·최근 대화 Popup | Fluent 입력, 전송/중단 위계, 포커스 복귀, 새 내용 표시 |
| 카드 | CapabilitiesPage | Wpf.Ui Card/Badge, 상태·버전·설명·검토 행동, 빈 상태 |
| 기능 만들기 | FeatureBuilderPage | 3단계 Wpf.Ui Card, 질문 입력, 검사 전 적용 차단, 상태 live region |
| 연결 | ConnectionsPage | Fluent 입력/ComboBox/ToggleSwitch, 연결 확인→저장 흐름, 비밀값 비표시 |
| 설정 | SettingsPage | Wpf.Ui ToggleSwitch, 필드 인접 오류, 연결 테스트, 위험 작업 분리 |
| 관리 | ActivityView, MemoryPage, SchedulePage, AgentJobsPage | 명령 모음, 정렬/필터, Wpf.Ui Badge, 빈 상태, 행별 작업 |
| 승인 | ToolApprovalWindow | FluentWindow, Card/Badge/InfoBar, 거부 기본 포커스, 전체 미리보기 스크롤 |
| 민감 문맥 | SensitiveContextPreviewWindow | FluentWindow, InfoBar, 이미지/문자 수정, 취소 기본 동작 |
| 편집 창 | AgentJobEditorWindow, ScheduleEditorWindow, MemoryEditorWindow | FluentWindow, 입력 이름, 검증 문구, 저장/취소 위계 |
| 기타 | CapabilityReviewWindow, AgentJobHistoryWindow, DiagnosticsWindow | FluentWindow, 스크롤 가능한 내용, 명확한 닫기/활성화 동작 |

## 3. 접근성과 반응형 기준

- 이름이 있는 모든 TextBox, RichTextBox, PasswordBox, ComboBox, CheckBox, ToggleSwitch와 아이콘 전용 Button은 접근성 이름을 가진다.
- 상태 문구는 `AutomationProperties.LiveSetting`을 사용하고 색상만으로 상태를 전달하지 않는다.
- 760×500 창에서 8개 통합 페이지 탐색, 핵심 입력, 정렬, 기능 만들기 이동과 키보드 흐름이 동작한다.
- 150~200% 배율에서 본문은 스크롤되고 주요 저장·취소 동작은 접근 가능해야 한다.
- 사용자 동작 줄이기 설정을 무시하는 앱 자체 Storyboard/Animation을 추가하지 않는다.

## 4. 자동 검증 근거

```powershell
dotnet test .\tests\LIGClaw.Desktop.Tests\LIGClaw.Desktop.Tests.csproj --filter "FullyQualifiedName~XamlResourceTests|FullyQualifiedName~UiConstructionTests"
.\scripts\smoke-ui.ps1 -Configuration Debug
.\scripts\verify.ps1
```

현재 UI 전용 결과:

- XAML 리소스·창 생성·접근성·축소 레이아웃: 32/32 통과
- UI smoke: IntegratedPages 8, CompactLayout 760×500, KeyboardNavigation passed, ApplicationErrors 0
- 전체 verify: Sidecar 141, Contracts 24, Application 454, Named Pipe 1, Desktop 850, build warning/error 0
- 서명 설치본: `artifacts/LIGClaw-0.6.0.2-Windows10-x64.zip` (`SHA-256 9f838cc8de2da79a1797cf266e21b00523613ba07039a91aec8d9d714283d3be`)

2026-08-17 회귀 수정:

- FluentWindow 전환 때 누락된 9개 독립 창의 Wpf.Ui `TitleBar`를 복구했다.
- 메인 창에서 최소화→복원과 닫기→트레이 상주를 실제 UI Automation으로 확인했다.
- 모든 FluentWindow의 TitleBar 존재·개수·높이를 XAML 회귀 테스트로 고정했다.
- 수정 후 UI 전용 테스트 32/32와 760×500 UI smoke를 통과했고 ApplicationErrors는 0이다.

## 5. Windows 10 실기 확인

자동 경계는 `net10.0-windows10.0.17763.0`, Windows 10 build 17763 이상, x64를 대상으로 한다. 패키지는 Wpf.Ui와 필요한 글꼴 대체 경로를 자체 포함하며 Windows 11 전용 창 효과를 요구하지 않는다.

최종 배포 승인 전 Windows 10 22H2 대상 PC에서 다음을 한 번 확인한다.

1. 관리자 설치 스크립트로 서명 인증서와 MSIX 설치가 완료된다.
2. 시작 메뉴에서 앱을 열었을 때 콤보박스, 토글, 카드와 한글이 정상 렌더링된다.
3. 100%와 200% 배율, 760×500 창에서 저장·취소·전송 버튼에 접근할 수 있다.
4. 기능 카드, 연결, 설정, 승인 창을 열고 키보드 Tab/Shift+Tab/Escape를 확인한다.
5. 앱 오류 로그가 0건이고 종료 후 다시 실행된다.
