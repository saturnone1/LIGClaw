# LIGClaw 개발 인수인계

> 갱신일: 2026-07-25  
> 작업 브랜치: `agent/phase1-desktop-foundation`

## 다른 PC에서 시작하기

```powershell
git clone https://github.com/saturnone1/LIGClaw.git
cd LIGClaw
git switch agent/phase1-desktop-foundation
git pull --ff-only
./scripts/verify.ps1
./scripts/run.ps1
```

필요한 개발 환경은 Node.js 24, .NET SDK 10.0.103 이상 패치 버전, PowerShell입니다. 정확한 SDK 선택은 `global.json`, Node 범위는 `sidecar/package.json`을 따른다.

API 키와 모델 연결 설정은 Git에 포함되지 않는다. 새 PC의 설정 화면에서 `Base URL`, `API Key`, `Model`을 다시 입력해야 하며 API 키는 해당 PC의 Windows 자격 증명 관리자에 저장된다. `%LOCALAPPDATA%\LIGClaw\Data\ligclaw.db`의 대화 기록도 PC 간 자동 동기화되지 않는다.

## 현재 완료 상태

- Phase 0 walking skeleton과 Phase 1 상주 셸 기반
- LIG CI 기반 WPF 테마, 앱 아이콘, 트레이 아이콘
- 단일 인스턴스, 트레이 상주, 시작프로그램, 사용자 선택형 전역 단축키
- 범용 OpenAI 호환 `Base URL/API Key/Model` 연결과 Credential Manager 저장
- Desktop 단독 SQLite 대화·이벤트 영속화와 최근 대화 복원
- Windows build 감지와 Windows 10/11 capability adapter
- Protocol 1.4 Desktop-issued `runId`, 이벤트·Tool 실행 격리, 중복 Tool 방지
- R0 `system.get_status.v1`의 모델 → Sidecar → Desktop → 모델 수직 경로
- 승인 판단을 `LIGClaw.Application` 정책으로 분리하고 Windows adapter는 실행만 담당

최근 전체 검증 기준으로 Sidecar 8개, Contracts 7개, Application 5개, Desktop 28개, Named Pipe 통합 1개 테스트가 통과하며 빌드 경고와 오류는 없다.

## 남은 Phase 1 작업

권장 구현 순서는 다음과 같다.

1. R0 `app.list_windows.v1`: Win32 공통 adapter와 Windows 10/11 fixture를 추가한다.
2. 일반 사용자용 승인 UI: Tool 이름 대신 “어떤 앱을 왜 여는지”를 보여주고 `이번 한 번` 승인부터 구현한다.
3. `system.show_notification.v1`: 실제 알림 방식은 Windows capability adapter로 분리하고 R1 정책을 통과시킨다.
4. R1 `app.launch.v1`: 실행 파일 임의 경로보다 등록 앱·정규화된 대상부터 지원한다.
5. 단축키 → 자연어 → 승인 → Windows 실행 → 결과 스트리밍 replay E2E를 Phase 1 완료 gate로 추가한다.

R1-R4 호출은 승인 UI가 없는 현재 상태에서 정책이 거부한다. 이는 라이선스나 임의 feature flag가 아니라 사용자 동의 없이 쓰기 동작을 수행하지 않기 위한 임시 안전 상태다. Windows Tool Host에는 R0 하드코딩이 없으므로 승인 결과를 정책에 전달하면 실행 adapter를 다시 설계할 필요가 없다.

## 알려진 제한과 확인 항목

### Windows 10 실기 검증

프로젝트의 기술적 target은 Windows 10 1809(build 17763)지만, 현재 자동 테스트는 Windows build profile을 모의한 검증이며 Windows 10 실기 실행을 대체하지 않는다. 다음 PC가 Windows 10이라면 우선 22H2에서 아래를 확인한다.

- 앱 시작, 단일 인스턴스와 기존 창 복원
- 트레이 아이콘 표시·복원·종료
- 전역 단축키 등록·변경·충돌 처리
- 시작프로그램 등록과 다음 로그인 시 트레이 시작
- Windows Credential Manager 저장·재로드
- SQLite 생성·WAL·앱 재시작 후 최근 대화 복원
- `system.get_status.v1`의 Windows 10 결과
- Sidecar 종료 후 자동 복구와 진행 중 대화 중단 처리

1809~21H2는 빌드 target만으로 지원을 확정하지 않는다. 실제 배포 runtime과 주요 기능을 검증한 뒤 지원표에 포함한다.

### npm low advisory

`npm audit`에는 `@cline/llms@0.0.65 → dify-ai-provider@1.1.1 → @ai-sdk/provider-utils@3.0.30` 경로의 uncontrolled resource consumption low advisory 1건이 남아 있다. 현재 LIGClaw가 Dify provider를 사용하지 않고 `npm audit fix --dry-run`도 변경 가능한 호환 버전을 제시하지 않아 `@ai-sdk/provider-utils` 4.x 강제 override는 적용하지 않았다.

다음 작업 PC에서 아래 명령으로 상태를 다시 확인한다.

```powershell
npm audit --prefix sidecar
npm ls @ai-sdk/provider-utils dify-ai-provider --prefix sidecar
```

Cline 업데이트 또는 호환되는 Dify provider가 나오면 contract/replay 전체 검증 후 수동으로 갱신한다.

## 아키텍처 주의사항

- 계약 변경은 `contracts/`를 먼저 수정하고 `npm run generate --prefix sidecar`로 C#과 TypeScript 생성물을 함께 갱신한다.
- Desktop이 Windows 효과, 승인, 비밀, SQLite를 소유한다.
- Sidecar가 보낸 위험 등급은 신뢰하지 않고 Desktop adapter의 canonical 위험 등급과 다시 비교한다.
- `conversationId`, `runId`, `toolCallId` 검증을 우회하는 실행 경로를 만들지 않는다.
- 프롬프트, API 키, 클립보드·파일 원문을 로그에 남기지 않는다.
- 작업 완료 전 루트에서 `./scripts/verify.ps1`을 실행한다.

관련 결정은 [ADR 0006](adr/0006-version-adaptive-windows-platform.md), [ADR 0008](adr/0008-desktop-tool-execution-bridge.md), [ADR 0009](adr/0009-desktop-issued-run-identity-and-tool-policy.md)를 먼저 읽는다.
