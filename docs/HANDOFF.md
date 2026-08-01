# LIGClaw 개발 인수인계

> 갱신일: 2026-08-01
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

- Phase 0 walking skeleton, Phase 1 상주 셸·대화, Phase 2 안전한 Windows 작업
- LIG CI 기반 WPF 테마, 앱 아이콘, 트레이 아이콘
- 단일 인스턴스, 트레이 상주, 시작프로그램, 사용자 선택형 전역 단축키
- 이름 있는 OpenAI 호환 모델 프로필, 프로필별 Credential Manager secret, 기본/fallback routing
- Desktop 단독 SQLite schema 7 대화·실행·이벤트·지속 승인 영속화와 최근 다중 턴 대화 복원
- 같은 대화의 안정적인 `conversationId`, 턴별 고유 `runId`, 명시적 `새 대화` 경계와 Sidecar Cline runtime 재사용
- Windows build 감지와 Windows 10/11 capability adapter
- Protocol 1.5 Desktop-issued `runId`, bounded conversation history, 이벤트·Tool 실행 격리, 중복 Tool 방지
- R0 `system.get_status.v1`, `app.list_windows.v1`의 모델 → Sidecar → Desktop → 모델 수직 경로
- R0 `system.get_storage_status.v1`, `system.get_resource_status.v1`, `system.get_network_status.v1`의 최소 데이터 시스템 관측
- R1 `system.show_notification.v1`, 등록 앱 한정 `app.launch.v1`과 일반 사용자용 `이번 한 번` 승인 UI
- 승인 판단을 `LIGClaw.Application` 정책으로 분리하고 Windows adapter는 미리보기와 실행만 담당
- 기본 빠른 호출 → 자연어 앱 실행 → 승인 → Windows 실행 → 결과 스트리밍 replay E2E
- Desktop Tool registry와 adapter별 canonical 위험도·capability·timeout
- SQLite 승인/실행 감사 기록, 실행 활동 화면과 파일 undo
- 창 활성화·정상 닫기, 안전한 파일 검색·열기·복사·이동·이름 변경·휴지통
- 승인 기반 클립보드 텍스트 읽기·쓰기와 민감 컨텍스트 안내
- 경로/reparse 재검증, 보호 위치·UNC 쓰기 차단, 덮어쓰기 금지, 최대 20개와 부분 실패 결과
- 자연어 R2 파일 이동 → 승인 → 실행 → 감사 → 결과 스트리밍 replay E2E
- 공급자 실패 원문 대신 고정 오류 코드만 IPC·SQLite·UI 경계를 통과하는 민감정보 차단
- 답변 Markdown을 WPF FlowDocument로 렌더링해 표·제목·강조·목록·인용·코드를 표시하고 원문은 SQLite에 유지
- Sidecar와 Desktop의 Tool별 제한 시간을 일치시켜 파일 복사·이동·휴지통 작업이 20초에 조기 종료되지 않도록 보강
- Agent/Tool 이벤트 처리 예외를 프로세스 종료 대신 안전한 실패 결과와 사용자 진단으로 격리
- 시작 메뉴 표시 이름만 반환하는 R0 설치 앱 검색과 승인 기반 R1 Explorer 현재 폴더·선택 항목 조회
- R0 파일 메타데이터와 승인·identity 재검증·128 KiB 상한·바이너리 거부가 적용된 R1 파일 텍스트 읽기
- Phase 3 개인 기억 `remember/list/forget`, 관리 화면 CRUD·JSON 내보내기, 출처·민감도·만료와 별칭·선호 인자 해석
- Phase 3 durable `schedule.create/list/cancel`, SQLite schema 5, 단발·매일·매주 반복, DST·놓친 실행 정책·재시작 복구와 예약 관리 화면
- Phase 3 안정화: 관리 창 XAML 리소스 회귀 검사, 선택 입력 기본값, 전체 목록 paging, 예약 시간대 표시, 절전 복귀 misfire·만료 lease 회수, 알림·UI 예외 격리, 실제 대화 출처 기록
- Phase 4 기반 UI Automation `inspect/invoke/set-value/send-text`, 5분 element handle, PID·프로세스 시작 시각·UIA runtime identity 재검증, foreground/focus guard, 비밀번호·사용자 입력 감지 차단
- WPF/native Win32 fixture의 창 이동 후 UIA 요소 재해석 통합 테스트와 좌표·임의 단축키 비노출
- Phase 5 Protocol 1.7 MCP 설정·상태·호출, exact-name allowlist, Desktop R3 승인·감사, Streamable HTTP/Bearer 인증과 고정 실행 ID stdio
- Phase 6 Protocol 1.8 bounded Web fetch/search, 구성형 검색 URL, cookie 없는 GET, redirect·본문·출력·콘텐츠 형식 제한과 Desktop R3 승인·감사
- Phase 6 Protocol 1.9 LIGClaw 전용 Edge profile, 새 창 identity 검증, 30분 만료 browser handle과 읽기 전용 접근성 snapshot
- Airgap A-4 Protocol 1.10 durable Agent job, SQLite schema 10 ledger·misfire·lease·재시도, 관리 UI와 background Sidecar executor
- Airgap A-5 Protocol 1.11 immutable per-run model routing, 제한 fallback과 명시 선택 no-fallback
- Airgap A-6 Protocol 1.12·SQLite schema 11 bounded local subagent, parent cancellation·권한 상한·결과 합류·orphan 정리
- B단계 Protocol 1.13 저장소·보안 관측, 폴더·UTF-8·ZIP 작업, 창 상태·설정·잠금/절전, 대화 범위 승인, Agent 작업 제어와 데이터 백업·복원
- C단계 Protocol 1.14 Windows 10/11 공통 전원 진단, 배터리 유무·잔량·충전·저전력/위험·에너지 절약 상태의 R0 최소 데이터
- 번들 읽기 전용 샘플 RAG, 연결별 health·timeout·장애 격리, 민감정보 제거 진단 번들, parent/crash recovery와 MCP·Desktop soak
- self-contained x64 MSIX, 번들 Node, SHA-256 서명 검증, App Installer 업데이트와 설치 수명주기 자동화

Windows 에이전트의 완료·부분·계획·후속 기능 전체 목록과 위험도별 실행 순서는 `docs/WINDOWS_AGENT_CAPABILITY_AUDIT.md`를 따른다.

최신 전체 검증 기준으로 Sidecar 80개, Contracts 7개, Application 20개, Desktop 281개, Named Pipe 통합 1개 테스트가 통과하며 빌드 경고와 오류는 없다. UI smoke도 통합 관리 페이지 5개, 760×500 compact layout, 포커스·검증·정렬·대화 경계·키보드 탐색과 Precision Workspace 동작을 통과했고 Sidecar handshake·heartbeat·restart·cleanup smoke도 통과했다. Phase 5 후속 안정화에서 진행 중 MCP 호출의 세션 폐기 경합, MCP 비밀의 원자적 저장·삭제, 진단 ZIP의 원자적 교체, 캐시된 설정 페이지 복귀, 관리 목록의 오래된 페이징 취소, UI smoke 종료 대기를 보강했다. 실사용 피드백 후에는 스트리밍 Markdown 렌더를 묶어서 처리해 스크롤 위치를 보존하고, AppsFolder의 패키지 앱 검색, 기억·예약 optional null 정규화, Desktop 기준 상대 예약 계산을 추가했다. native Tool call 대신 인자 JSON 텍스트를 반환하는 OpenAI 호환 모델에는 명시적 사용자 의도와 정확한 allowlist 스키마가 일치하는 앱 실행·기억·예약만 Desktop 승인 경로로 복구하는 제한적 호환 계층을 적용했다. 승인 검토와 실제 Tool 실행의 timeout을 분리해 Sidecar의 대화형 대기 정책을 단일화했으며, 모델 공백 응답과 JSON 복구 실행 취소도 결정적으로 종료 상태를 남긴다. 최신 안정화에서는 사용자 취소와 연결 종료를 Desktop Tool까지 전파하고, Tool 승인·실행을 직렬화했으며, 긴 transcript의 Markdown 렌더 빈도를 제한하고 새 응답 표시 판정을 실제 렌더 이후로 이동했다. 에어갭 확장 A-1에서 대화 trigram FTS 검색을 추가했고 A-2에서 기본 비활성 의미 기억, schema 9 vector 색인, keyword fallback과 설정 UI를 추가했다. A-3에서는 bounded Web fetch/search와 구성형 검색 공급자, 전용 Edge profile과 읽기 전용 접근성 snapshot을 추가했다. A-4~A-6에서는 durable Agent 작업, immutable 모델 routing과 제한 fallback, bounded 로컬 하위 Agent를 추가했다. B단계에서는 저장소·보안 관측, 파일 생산성, 창·설정·세션 제어, 대화 범위 승인, Agent 작업 제어와 데이터 백업·복원을 추가했다. 네트워크 주소 대역은 앱에서 인위적으로 차단하지 않는다.

## 현재 목표: C단계 출시 후보 안정화와 유지보수성

상세 구현 순서와 완료 조건은 `docs/C_STAGE_PLAN.md`를 단일 작업 계획으로 사용한다. 현재 PC에서 `v0.6.0` 전체 verify와 Sidecar/UI smoke를 통과했다. Windows PowerShell 5.1이 BOM 없는 한국어 스크립트를 잘못 해석하는 회귀는 BOM 규칙과 검증 guard로 복구했다. C-1.1에서 Sidecar 내장 Tool 51개의 등록 metadata를 단일화하고 system/app/file/context/schedule/automation/web/agent 모듈로 분리했으며 initialize capability도 같은 metadata에서 생성한다. C-1.2에서는 conversation/run 수명·취소, Agent event 표시, Tool 실행 직렬화, 최근 검색 최신 요청 취소, Sidecar 시작·취소와 run persistence 순서를 독립 controller/interface로 옮겼고 persistence 중 취소 경합을 수정했다. C-1.3에서 schema/connection/backup 소유권을 `ConversationDatabase`로 좁히고 conversation, operational audit/grant/undo, 개인 기억·의미 벡터, 예약·misfire·lease, durable Agent 작업, 로컬 하위 Agent SQL을 전용 repository adapter로 분리했다. composition root와 replay 테스트는 각 interface adapter를 직접 사용한다. C-1.4에서는 비동기 작업 수명과 모델·MCP·의미 기억·Web 검색·시작프로그램·단축키 흐름을 전용 guard/controller로 분리하고 의미 기억의 registry/Credential Manager 저장을 원자화했다. C-2에서는 Windows PowerShell 5.1 CI parse, release manifest fixture, 격리된 verify 출력, one-command release candidate와 package evidence 생성을 추가했다. MCP cycle, Desktop/Sidecar 재시작, DB 복구, resume reconciliation, 진단 redaction 결과는 bounded JSON으로 수집하고 실제 sleep/resume는 외부 수동 gate로 분리했다. C-4 첫 수직 기능으로 Protocol 1.14 R0 전원 진단을 추가했다. 최신 전체 verify는 실행 중인 LIGClaw와 충돌 없이 Sidecar 81개, Contracts 7개, Application 20개, Desktop 291개, Named Pipe 통합 1개가 모두 통과했고 빌드 경고·오류는 없다. 현재 PC에는 Windows SDK MakeAppx가 없어 실제 unsigned MSIX 재생성은 SDK 설치 환경에서 대기하며, UI smoke와 Desktop restart evidence도 실행 중인 사용자 LIGClaw 프로세스를 종료하지 않기 위해 대기한다.

## 계속 추적할 Phase 5 외부 gate

Phase 5 제품 구현과 로컬 자동 acceptance는 완료했다. `mcp.configure/status/call`, 읽기 전용 RAG, Credential Manager auth, 고정 executable registry stdio, 진단, crash recovery, soak, MSIX·서명·App Installer 자동화가 준비됐다. 자세한 증거는 `PHASE5_SECURITY_ACCEPTANCE.md`, 배포 절차는 `runbooks/phase5-msix-release.md`, 경계는 ADR 0015를 따른다.

남은 release gate는 production/machine-trusted 서명 체인이 준비된 깨끗한 Windows 계정에서 설치 → 앱 실행 → 로그인 자동 시작 → 상위 버전 업데이트 → 제거를 실행하는 것이다. 현재 비관리자 개발 셸의 CurrentUser 임시 루트는 AppX Deployment Service가 `0x800B0109`로 거부했으며 테스트 인증서·패키지·프로세스는 모두 정리됐다. 이 외부 gate가 끝날 때까지 Phase 5를 배포 완료로 표시하지 않는다.

## 완료 목표: UI/UX 전면 개선

`docs/UI_UX_REDESIGN_PLAN.md`의 UX-0부터 UX-5까지 완료했다. 설정·활동·기억·예약은 MainWindow 내부 페이지이며, 760px compact layout, 선택 상태, 홈 포커스 복원, 사용자 요청·응답·실행 진행 타임라인, 하단 composer, 연결 복구 banner가 동작한다. 관리 목록, 연결 테스트 선행 저장, 접근성, 대량 목록 가상화, 안전한 Markdown 링크와 UI 이벤트 예외 안전망을 자동 검증한다. 후속 안정화로 대화 세션 연속성, 명시적 새 대화, 재시작 복원과 실패 턴 보존을 추가했다. 사용자 결정에 따라 별도 배율·물리 다중 모니터 검증은 필수 완료 조건이 아니다. 세부 증거는 `docs/UI_UX_ACCEPTANCE.md`를 따른다.

## 완료 목표: Precision Workspace v3

`docs/UI_UX_V3_PLAN.md`의 V3-0부터 V3-7까지 완료했다. LIG Defense&Aerospace 공식 홈페이지와 CI의 다크 블루·메탈릭 그레이, 굵은 정보 위계, 전진 사선, 큰 여백을 Windows 작업 지휘 공간으로 번역했다. adaptive AppRail, command header 대화 전환기, hero/docked composer, 문서형 대화와 Action Card, 새 응답 indicator, 공용 관리 surface, 승인·복구 정보 계층을 적용했으며 정량 결과는 `docs/UI_UX_ACCEPTANCE.md`를 따른다.

## 함께 닫을 항목: Phase 4 UI Automation 실기 acceptance

Phase 4의 schema-first 기반과 안전 경로는 구현했다. 다음 작업은 장치·앱별 호환성 acceptance를 닫는 것이다.

1. 메모장·계산기·설정 등 대표 Windows 앱의 UIA provider 호환표를 만든다.
2. 승인 창 직후 foreground 전환, 조작 도중 사용자 입력, provider hang을 실기 fault test로 확인한다.
3. 좌표 fallback은 별도 capability와 별도 승인 설계 전까지 추가하지 않는다.

exact-scope R1 동작은 `이번 한 번`, 메모리에만 남는 `이 대화 동안`, 또는 30일 지속 승인을 선택할 수 있고 지속 승인은 설정에서 개별 철회한다. 인자나 대상이 달라지면 다시 승인하며 앱 재시작 시 대화 범위 승인은 사라진다. R2/R3/R4와 클립보드·파일 내용·UI Automation·외부 MCP는 매 호출 승인을 유지한다.

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
- 저장소·리소스·네트워크 R0 관측 Tool의 Windows 10 결과
- `app.list_windows.v1`의 창 필터링과 foreground/minimized 결과
- 설치 앱 표시 이름 검색과 Explorer 현재 폴더·선택 항목 승인 조회
- 파일 메타데이터와 128 KiB 제한 UTF 텍스트 읽기
- `system.show_notification.v1`의 알림 표시와 `app.launch.v1`의 Start Menu 등록 앱 실행
- 단발·반복 예약 실행, 트레이 재시작 복구와 `ask` 관리 화면 결정
- 창 활성화·정상 닫기와 승인 직전 identity 재검증
- 파일 장경로·reparse·충돌·부분 실패·휴지통·undo
- 클립보드 읽기·쓰기와 실행 활동 화면
- UIA inspect/invoke/set-value/send-text의 identity·foreground·focus guard와 비밀번호 차단
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
- `conversationId`는 대화 수명, `runId`는 한 요청 수명으로 유지하고 `toolCallId`까지 어떤 검증도 우회하지 않는다.
- 프롬프트, API 키, 클립보드·파일 원문을 로그에 남기지 않는다.
- 작업 완료 전 루트에서 `./scripts/verify.ps1`을 실행한다.

관련 결정은 [ADR 0006](adr/0006-version-adaptive-windows-platform.md), [ADR 0008](adr/0008-desktop-tool-execution-bridge.md), [ADR 0009](adr/0009-desktop-issued-run-identity-and-tool-policy.md)를 먼저 읽는다.
