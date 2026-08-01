# LIGClaw C단계 계획 — 출시 후보 안정화와 유지보수성

> 상태: 진행 중
> 기준일: 2026-08-01
> 기준 버전: `v0.6.0` (`d8f71c5`)
> 목표 브랜치: `agent/phase1-desktop-foundation`

## 목표

Phase 0~7에서 확보한 기능을 유지하면서 LIGClaw를 실제 사내 배포 가능한 출시 후보로 만든다. 다음 기능 수를 늘리는 것보다 먼저 현재 PC의 검증 기준선, Windows 10/11 배포 증거, 코드 책임 분리와 변경 안전성을 닫는다.

성공 기준은 다음과 같다.

- Windows 11 개발 PC에서 전체 회귀, Sidecar smoke, UI smoke, bounded soak가 반복 통과한다.
- Windows PowerShell 5.1과 PowerShell 7에서 모든 배포·검증 스크립트가 동일하게 파싱된다.
- 큰 composition 파일을 책임별 경계로 분리해 새 Tool 하나를 추가할 때 관련 schema·adapter·policy·test만 읽으면 된다.
- Windows 10 22H2와 깨끗한 Windows 11 표준 사용자 계정에서 설치·시작·업데이트·제거 evidence를 남긴다.
- 출시 차단 항목과 의도적으로 제공하지 않는 고위험 기능을 구분한다.

## 실행 원칙

1. C-0과 C-1이 끝날 때까지 새로운 고위험 Windows 쓰기 기능을 추가하지 않는다.
2. 리팩터링은 계약과 동작을 바꾸지 않는 작은 수직 변경으로 나누고 매 단계에서 전체 replay를 유지한다.
3. 외부 장비·인증서가 필요한 검증은 로컬 구현과 분리해 gate로 추적한다.
4. 범용 shell, 권한 상승, 영구 삭제, 좌표 기반 무제한 자동화는 기능 누락으로 보지 않는다.
5. 새 기능은 일반 사용자의 반복 작업 가치, 최소 데이터, Windows 10/11 공통성 순서로 선택한다.

## C-0 — 기준선 복구와 문서 일치

상태: 완료 (2026-08-01). Windows PowerShell 5.1의 UTF-8 오독을 막는 BOM 규칙과 검증 guard를 추가했고, UI validation을 bounded polling으로 바꾼 뒤 전체 verify·Sidecar/UI smoke를 통과했다.

### 작업

1. `v0.6.0`을 현재 PC에 fast-forward하고 `./scripts/verify.ps1`을 실행한다.
2. Sidecar handshake·heartbeat·restart·cleanup smoke를 실행한다.
3. UI smoke를 Windows PowerShell 5.1과 PowerShell 7 양쪽에서 파싱·실행한다.
4. 비 ASCII PowerShell 스크립트는 UTF-8 BOM을 필수화하고 `verify.ps1`에서 drift를 거부한다.
5. 비동기 UI 검증은 고정 sleep 대신 bounded polling을 사용한다.
6. 구현 계획, 기능 감사, 보안 acceptance의 테스트 수와 상태 표기를 현재 기준선으로 맞춘다.

### 완료 조건

- Sidecar 78, Contracts 7, Application 20, Desktop 248, Named Pipe 통합 1 테스트 통과
- Sidecar smoke와 760×500 UI smoke 통과
- 빌드 경고·오류와 PowerShell parser 오류 0건
- `git diff --check`와 계약 생성 drift 검사 통과

## C-1 — AI 에이전트 친화적 책임 분리

상태: C-0 이후 착수.

현재 비생성 source의 주요 집중 지점은 `cline-agent-runtime-adapter.ts` 약 2,200줄, `ConversationStore.cs` 약 2,000줄, `MainWindow.xaml.cs` 약 1,500줄이다. 단순 파일 쪼개기가 아니라 변경 이유와 테스트 경계를 기준으로 분리한다.

### C-1.1 Sidecar Tool catalog 분리

상태: 완료 (2026-08-01).

- built-in Tool 선언을 system/app/file/context/memory/schedule/automation/web/agent 영역으로 나눈다.
- Cline session·provider routing과 Tool catalog 조립을 분리한다.
- Desktop canonical Tool name과 Sidecar 노출 목록의 누락·중복을 결정적 테스트로 고정한다.
- text-only JSON fallback allowlist는 native Tool catalog와 동일한 metadata 원천을 사용하게 한다.

완료 조건: 공개 프로토콜 변경 없이 Sidecar 78개 이상과 Named Pipe 통합 테스트가 통과하고, 신규 Tool 등록 지점이 한 곳으로 수렴한다.

구현 결과: Cline 세션·공급자 라우팅에서 내장 Tool 조립을 분리했고, system/app/file/context/memory/schedule/automation/web/agent 영역 모듈과 공통 스키마 모듈로 나눴다. 모델 노출명과 Desktop canonical 이름 50개는 `tool-registration.ts` 한 곳에서 등록하며 실제 catalog의 누락·중복과 text-only JSON fallback allowlist 일치를 결정적 테스트로 고정했다.

### C-1.2 Desktop 대화 orchestration 분리

상태: 완료 (2026-08-01).

- `MainWindow`에서 conversation/run 수명, 취소, Sidecar event 처리, Tool 진행 상태를 controller로 이동한다.
- WPF control 조작과 화면 전환만 code-behind에 남긴다.
- transcript rendering, recent conversation refresh, stale search cancellation을 독립적으로 테스트한다.
- 조립은 composition root에서 명시하며 service locator나 숨은 singleton을 추가하지 않는다.

구현 결과: conversation/run 수명과 취소, Agent event 표시 정책, Tool 실행 직렬화·진행 상태, 최근 검색의 최신 요청 취소, Sidecar 시작·취소와 대화 run persistence 순서를 독립 controller/interface로 분리했다. 취소가 persistence 처리 중 발생해 UI run이 남던 경합도 terminal 상태와 `cancelled` 기록으로 닫았으며, code-behind에는 WPF 표시·전환과 명시적 조립만 남겼다.

완료 조건: 기존 대화 연속성·취소·Tool 직렬화·스크롤 회귀 테스트와 UI smoke가 동작 변경 없이 통과한다.

### C-1.3 SQLite repository와 migration 분리

상태: 진행 중. 1차 슬라이스에서 schema 1~11 migration, 단일 connection/gate, pending restore와 backup 검증을 `ConversationDatabase` 단독 소유로 옮겼다. 미래 schema 거부 후 파일 잠금 해제를 회귀 테스트로 추가했고 저장소 경계는 ADR 0026에 기록했다. 2차 슬라이스에서 conversation run/event/search/transcript SQL을 `ConversationRepository`로, 3차 슬라이스에서 감사·승인 grant·undo SQL을 `OperationalAuditRepository`로 옮겼다. composition root와 직접 replay 테스트는 facade 대신 각 interface adapter를 사용한다. 다음은 기억과 예약 repository를 분리한다.

- connection·transaction·schema migration 소유자를 `ConversationDatabase` 경계로 좁힌다.
- 대화/검색, 감사·승인·undo, 기억, 예약, Agent job, subagent repository를 인터페이스별 adapter로 분리한다.
- SQLite 파일과 connection은 계속 Desktop 단독 소유이며 repository가 개별 connection이나 migration을 만들지 않는다.
- schema 1~11 upgrade, 손상·미래 버전 거부, backup/restore 테스트를 유지한다.

완료 조건: 기존 DB를 그대로 열 수 있고 모든 repository·scheduler·복원 테스트가 통과하며 schema version은 바뀌지 않는다.

### C-1.4 설정 화면 책임 분리

- 모델 프로필, MCP, 의미 기억, Web 검색, 시작프로그램·단축키 저장 흐름을 section controller로 분리한다.
- 저장 전 연결 테스트, secret rollback, stale operation cancellation 규칙을 공통 operation guard로 유지한다.

완료 조건: 설정 정책·Credential Manager rollback·UI validation 테스트와 UI smoke가 통과한다.

## C-2 — 로컬 출시 후보 자동화

상태: C-1과 병행 가능한 저위험 작업부터 진행.

1. CI에서 `verify.ps1` 외에 Windows PowerShell 5.1 script parse 검사를 실행한다.
2. self-contained x64 MSIX를 무서명 상태까지 재현 가능하게 만들고 파일 목록·Node 번들·manifest를 검사한다.
3. 릴리스 산출물에 SHA-256 목록, 버전, protocol/schema, 검증 결과를 담은 manifest를 생성한다.
4. Sidecar restart 10회, MCP cycle, sleep/resume와 DB backup/restore smoke 결과를 릴리스 evidence로 남긴다.
5. 진단 번들에 prompt·API key·사용자 경로·파일 원문이 없는지 fixture로 재검증한다.

완료 조건: 새 checkout에서 한 문서의 명령만으로 동일한 unsigned release candidate와 검증 보고서를 생성한다.

## C-3 — 외부 환경 release gate

상태: 필요한 PC와 서명 체계가 준비되면 실행. 로컬 기능 개발과 별도 추적한다.

### Windows 11 깨끗한 계정

- machine-trusted production 서명으로 설치
- 첫 실행, Credential Manager, 트레이, 전역 단축키
- 로그인 자동 시작
- App Installer 상위 버전 업데이트
- 제거 후 패키지·시작프로그램·프로세스 잔존 여부 확인

### Windows 10 22H2

- `docs/HANDOFF.md`의 Windows 10 기능 matrix 실행
- Windows 11 전용 DWM 호출이 선택되지 않는지 확인
- 알림, Explorer COM, UIA, Credential Manager, 시작프로그램과 SQLite 복구 확인
- 통과 전에는 1809~21H2를 지원 완료로 표시하지 않는다.

### UI Automation 실제 앱 matrix

- 메모장, 계산기, 설정과 대표 사내 앱에서 inspect/invoke/set-value/send-text 결과 기록
- 승인 직후 foreground 변경, 사용자 입력 개입, provider timeout을 검증
- 호환 실패는 앱 전체 실패가 아니라 해당 provider의 구조화된 unavailable로 격리

완료 조건: 서명된 설치 수명주기와 OS/UIA matrix 결과가 `docs/PHASE5_SECURITY_ACCEPTANCE.md`에 기록된다.

## C-4 — 다음 사용자 가치 기능

상태: C-1 완료 후 한 항목씩 수직 구현. 아래 순서는 권장 우선순위다.

1. **전원 진단 확장**: 배터리 잔량·충전 상태·예상 절전 상태를 R0 최소 데이터로 제공한다.
2. **느린 PC 진단**: CPU·메모리 상위 프로세스를 bounded 결과로 제공한다. 프로세스 이름 노출은 R1 문맥 승인으로 분류한다.
3. **네트워크 진단 상세**: IP/DNS/게이트웨이·Wi-Fi SSID를 R1로 조회하되 자격 증명과 전체 주소 이력은 저장하지 않는다.
4. **장치 상태**: 기본 오디오 출력·디스플레이·프린터 상태를 R0로 시작하고 변경은 별도 R1/R2 Tool로 분리한다.

각 기능 완료 조건은 schema-first 계약, Windows 10/11 capability adapter, canonical 위험도, 최소 데이터 결과, provider unavailable 격리, Sidecar replay와 Desktop adapter 테스트다.

## C-5 — 명시적 사용자 문맥과 입출력

상태: C-4 이후 별도 개인정보 ADR을 먼저 작성한다.

1. 선택 영역 또는 현재 창의 1회 화면 캡처와 OCR
2. 전송 전 preview·마스킹과 메모리 비보존 기본값
3. opt-in 음성 입력과 TTS, 방해 금지 시간
4. Explorer 선택 텍스트/우클릭 진입점
5. opt-in 반복 작업 제안

화면·음성 원문은 기본적으로 SQLite·진단·감사에 저장하지 않는다. 좌표 fallback, 인증 입력, 백그라운드 상시 캡처는 별도 승인 설계 없이는 추가하지 않는다.

## 의도적으로 보류하는 기능

- 임의 PowerShell·cmd·사용자 생성 script 실행
- 관리자 권한 자동 상승과 서비스 설정 변경
- 영구 삭제와 무제한 덮어쓰기
- 로그아웃·재시작·종료 자동 실행
- 좌표만 사용하는 범용 데스크톱 자동화
- 클립보드·화면의 상시 감시

이 항목은 과한 제약이 아니라 영향 범위, 복구, 사용자 동의가 현재 typed Tool 정책으로 증명되지 않은 기능이다. 실제 요구가 확인되면 각각 독립 계약과 위험도·preview·undo 가능성을 먼저 설계한다.

## 의존성과 유지보수

- `@cline/*`는 0.0.x exact pin을 유지하고 계약/replay 전체 통과 없이 올리지 않는다.
- 미사용 Dify provider 하위 `@ai-sdk/provider-utils` low advisory는 실행 노출을 재확인하며 호환 upstream이 나오면 수동 갱신한다.
- .NET, Node, MCP SDK와 Windows 지원표는 release candidate마다 공식 지원 상태를 다시 확인한다.
- dependency 변경과 기능 변경을 같은 커밋에 섞지 않는다.

## 전체 순서

```text
C-0 기준선 복구
  ├─ C-1 책임 분리 ── C-2 로컬 출시 자동화 ── C-4 사용자 가치 기능 ── C-5 문맥·입출력
  └─ C-3 외부 OS·서명·UIA gate (환경 준비 시 병행)
```

다음 실제 구현 진입점은 C-1.3 SQLite repository 분리다. 먼저 schema migration·connection lifetime을 보존한 채 conversation run/query repository를 monolith에서 분리하고 동일 DB reopen·replay 테스트로 고정한다.
