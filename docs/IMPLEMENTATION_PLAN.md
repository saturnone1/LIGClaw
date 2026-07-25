# LIGClaw 구현 계획

> 문서 상태: 초안 v1
> 작성 기준일: 2026-07-25
> 제품 정의: Windows 사용자 세션에 상주하며 반복 작업을 대신하는 로컬 우선 개인 비서

## 1. 결론

LIGClaw는 **.NET 10 WPF 데스크톱 호스트 + 격리된 Node.js 에이전트 사이드카 + 호스트 단독 소유 SQLite**로 구현한다.

핵심 설계 원칙은 다음과 같다.

1. Windows 기능, 권한, 예약 실행, 데이터는 데스크톱 호스트가 소유한다.
2. Node 사이드카는 LLM 대화 루프와 MCP 연결만 담당하며 언제든 재시작·교체할 수 있어야 한다.
3. 모든 동작은 타입이 명확한 Tool 계약으로만 통과한다. LLM에 범용 PowerShell을 기본 노출하지 않는다.
4. Tool 실행은 `검증 → 정책 판정 → 미리보기/승인 → 실행 → 감사 기록`의 한 경로를 사용한다.
5. 기능은 기술 계층이 아니라 작은 수직 슬라이스 단위로 추가한다.
6. 개인 정보는 로컬 우선, 최소 수집, 명시적 기억, 선택적 컨텍스트를 기본값으로 한다.

초기 제품의 성공 기준은 “대화가 되는 앱”이 아니라 아래 시나리오를 안전하고 반복 가능하게 수행하는 것이다.

- 전역 단축키로 비서를 열어 계산기를 실행한다.
- 활성 창이나 파일 탐색기 선택 항목을 사용자의 허용 아래 문맥으로 조회한다.
- 파일 이동 계획을 먼저 보여주고 승인 후 실행하며, 가능한 작업은 되돌릴 수 있다.
- 30분 뒤 알림과 반복 알림을 앱 재시작 후에도 정확히 수행한다.
- 사용자가 명시한 별칭을 기억하고 조회·수정·삭제한다.
- MCP 서버가 없거나 Node 사이드카가 죽어도 트레이, 설정, 예약 알림, 로컬 관리 화면은 동작한다.

## 2. 범위

### 2.1 MVP에 포함

- 트레이 앱, 대화창, 빠른 입력창, 전역 단축키, 설정, 승인 창
- 스트리밍 대화와 작업 진행 표시
- Windows 상태 조회와 알림
- 앱/창 조회, 실행, 활성화
- 파일 조회, 검색, 열기, 복사, 이동, 이름 변경, 휴지통 이동
- 클립보드 텍스트 읽기/쓰기(명시적 호출)
- Windows UI Automation 기반 요소 조회와 제한된 조작
- 단발/반복 알림 및 작업 취소
- 명시적 개인 메모리와 최근 대화
- stdio 및 Streamable HTTP MCP 클라이언트
- 실행 전 승인 정책, 감사 로그, 민감값 마스킹

### 2.2 후속 범위

- OCR, 화면 캡처, VLM 분석
- 탐색기 우클릭 확장과 선택 텍스트 호출
- 브라우저 탭/페이지 자동화
- 음성 입력과 TTS
- 클립보드 변화 감지 기반 제안
- 반복 행동 자동화 제안
- 선택형 고급 `shell.execute`

### 2.3 기본 제품에서 제외

- RAGFlow 직접 연동: `search_knowledge`, `get_document` 등을 제공하는 MCP 서버로 연결
- Jira, GitHub, Docker, Kubernetes, DB, Office/HWP/CAD 등 제품별 통합: MCP로 제공
- Windows Service, 무인 세션, 관리자 권한 자동 상승
- 모델이 임의 생성한 스크립트의 무승인 실행
- 좌표/VLM만 사용하는 범용 데스크톱 자동화
- 코딩 에이전트를 기본 모드로 제공하는 것

## 3. 아키텍처

```text
┌──────────────────────── Assistant.Desktop (.NET 10/WPF) ────────────────────────┐
│ UI: Tray · Chat · Quick Input · Approval · Settings · Activity                  │
│ Application: Conversation · Tool Pipeline · Policy · Scheduler · Memory         │
│ Infrastructure: Windows APIs · UIA · SQLite · Notifications · Credential Locker │
│                                                                                  │
│ SidecarSupervisor ── Named Pipe / JSON-RPC 2.0 ──┐                               │
└───────────────────────────────────────────────────┼───────────────────────────────┘
                                                    │
┌────────────────────── Assistant.Sidecar (Node.js) ┴──────────────────────────────┐
│ AgentRuntimePort implementation · Prompt assembly · Tool proxy · MCP client      │
│ Cline adapter (version pinned) · Provider adapters · event streaming              │
└───────────────────────────────┬───────────────────────────────────────────────────┘
                                │ MCP
                 ┌──────────────┴──────────────┐
                 │ External/product/RAG tools │
                 └─────────────────────────────┘
```

### 3.1 프로세스 책임

| 책임 | Desktop | Sidecar |
|---|---:|---:|
| 트레이/UI/전역 단축키 | 소유 | - |
| Windows Tool 실제 실행 | 소유 | 요청만 |
| 승인과 정책의 최종 판정 | 소유 | 힌트만 |
| SQLite와 스키마 마이그레이션 | 단독 소유 | RPC로 요청 |
| 예약 작업과 알림 | 소유 | 자연어 해석 보조 |
| LLM 공급자와 Agent loop | - | 소유 |
| MCP 세션/Tool 발견 | 상태 표시 | 소유 |
| 비밀 저장 | Credential Locker 소유 | 필요 시 단기 전달 |
| 감사 로그 | 단독 소유 | 이벤트 전달 |

SQLite 파일을 두 프로세스가 공유하지 않는다. 이 규칙으로 잠금 경쟁, 이중 마이그레이션, 사이드카 버전에 따른 데이터 손상을 막는다.

### 3.2 UI 기술 선택

MVP는 **WPF on .NET 10**을 선택한다. 트레이, 전역 단축키, HWND/Win32, UI Automation과의 통합이 성숙하고 자동화 테스트 자료가 풍부하기 때문이다. Windows App SDK는 알림 등 필요한 기능만 점진적으로 사용한다. UI를 `View → ViewModel → Application Port`로 분리해 향후 WinUI 3로 옮겨도 도메인과 Tool 구현은 유지한다.

### 3.3 에이전트 런타임 선택

기본 후보는 정확히 고정한 `@cline/agents@0.0.65`, `@cline/llms@0.0.65`이며 외부로 직접 노출하지 않는다.

```text
ConversationService → IAgentRuntime (JSON-RPC 계약)
                           ↑
                 ClineAgentRuntimeAdapter
```

0.0.x/experimental API 위험 때문에 Phase 0에서 아래를 스파이크하고 통과한 경우에만 확정한다.

- 사용자 정의 Tool 등록과 구조화 입력/출력
- 토큰/텍스트/Tool 이벤트 스트리밍
- Tool 실행 전후 hook과 취소
- 대화 재개 또는 호스트 주도 대화 이력 주입
- MCP 클라이언트 통합 방식
- 공급자별 인증정보를 장기 보유하지 않는 구동 방식

실패하면 `@cline/sdk@0.0.65` 어댑터를 비교 구현한다. 어느 쪽이든 Desktop 계약과 Tool 스키마는 바꾸지 않는다. 버전 업그레이드는 Dependabot 자동 병합 대상에서 제외하고 계약/리플레이 테스트 통과 후 수동 승인한다.

## 4. 저장소와 의존성 구조

```text
LIGClaw.sln
├─ src/
│  ├─ LIGClaw.Desktop/              # WPF composition root와 UI
│  ├─ LIGClaw.Application/          # use case, orchestration, ports
│  ├─ LIGClaw.Domain/               # 정책/메모리/작업의 순수 모델
│  ├─ LIGClaw.Windows/              # Win32, UIA, 파일, 앱, 클립보드
│  ├─ LIGClaw.Persistence/          # SQLite, migrations, repositories
│  └─ LIGClaw.Contracts/            # 생성된 C# IPC/Tool DTO
├─ sidecar/
│  ├─ src/runtime/                  # IAgentRuntime 구현
│  ├─ src/mcp/                      # MCP 연결과 Tool 변환
│  ├─ src/transport/                # Named Pipe JSON-RPC
│  └─ src/generated/                # 생성된 TypeScript 계약
├─ contracts/
│  ├─ rpc/*.schema.json             # IPC 단일 원천
│  └─ tools/*.schema.json           # Tool 입출력 단일 원천
├─ tests/
│  ├─ Domain.Tests/
│  ├─ Application.Tests/
│  ├─ Windows.IntegrationTests/
│  ├─ Persistence.Tests/
│  ├─ Contract.Tests/
│  ├─ sidecar/
│  └─ fixtures/replays/              # 모델 없이 재현할 이벤트 기록
├─ docs/
│  ├─ adr/                           # 중요한 결정과 이유
│  ├─ tools/                         # 사용자 의미/위험/예시
│  └─ runbooks/                      # 진단, DB 복구, 배포
└─ scripts/                          # build, contract generation, verification
```

의존성은 항상 바깥에서 안쪽으로 향한다.

```text
Desktop / Windows / Persistence / Sidecar adapters
                         ↓
                    Application
                         ↓
                       Domain
```

Domain과 Application은 WPF, SQLite, Cline SDK, MCP SDK를 참조하지 않는다. 기능 하나는 가능하면 `Command/Handler/Validator/Tests`가 인접한 수직 슬라이스로 둔다. 거대한 `ToolService`, `Utils`, 전역 service locator는 만들지 않는다.

## 5. Tool 계약과 실행 파이프라인

### 5.1 Tool 정의

각 Tool은 다음 메타데이터를 가진 선언적 manifest로 등록한다.

- 안정적인 이름과 버전: `file.move.v1`
- JSON Schema 입력/출력
- 사용자에게 보여줄 동작 설명 생성기
- 위험 등급과 필요한 capability
- timeout, 취소 가능 여부, 멱등성
- dry-run/preview 및 undo 지원 여부
- 민감 필드와 로그 마스킹 규칙

JSON Schema에서 C# record와 TypeScript type을 생성하고 CI에서 생성물 drift를 검사한다. LLM 설명, 승인 화면, 검증기, 감사 로그가 동일한 계약을 사용한다.

### 5.2 실행 경로

```text
Agent/MCP 요청
  → schema validation
  → tool/capability allowlist
  → canonical path·target resolution
  → policy evaluation
  → preview 생성
  → 필요 시 사용자 승인(실제 인자와 영향 표시)
  → timeout/cancellation을 포함한 실행
  → 구조화 결과 + 사용자용 요약
  → 마스킹된 append-only audit event
```

사이드카는 정책 결과를 위조할 수 없다. 최종 승인과 Windows 실행은 Desktop 프로세스에서만 이루어진다.

### 5.3 위험 등급 기본값

| 등급 | 예 | 기본 동작 |
|---|---|---|
| R0 읽기 | 시간, 배터리, 앱 목록 | 허용, 화면/클립보드는 별도 동의 |
| R1 가역 쓰기 | 앱 실행, 클립보드 쓰기, 폴더 열기 | 세션 정책에 따라 허용 |
| R2 영향 있는 쓰기 | 파일 이동/덮어쓰기, 창 닫기, UI 입력 | 미리보기와 명시적 승인 |
| R3 민감/광범위 | 다수 파일 변경, 외부 전송, 인증 작업 | 매번 상세 승인, 제한된 범위 |
| R4 금지 | 영구 삭제, 권한 상승, 임의 shell | 기본 비활성; 설정+실행 시 이중 확인 |

승인은 `이번 한 번`, `이 대화 동안`, `이 Tool+제약에 대해 항상`만 허용한다. “항상 허용”에는 경로, 앱, 서버 같은 구체적 scope와 만료를 저장한다.

### 5.4 파일 안전

- 모든 경로는 실행 전에 절대 경로로 정규화하고 junction/symlink 최종 대상을 확인한다.
- 삭제 기본 동작은 휴지통 이동이다.
- 덮어쓰기는 기본 금지하며 preview에 충돌을 표시한다.
- 대량 작업은 항목 수/총 용량/대상 루트를 요약하고 batch 상한을 둔다.
- 가능한 Tool은 실행 전 undo journal을 만들고 Activity 화면에서 되돌리기를 제공한다.

## 6. IPC와 장애 격리

Named Pipe 위에 길이 프레이밍된 JSON-RPC 2.0을 사용한다. 프로토콜은 `initialize` handshake로 버전, 지원 capability, 앱 빌드, 계약 해시를 교환한다.

필수 메시지는 다음과 같다.

- Desktop → Sidecar: `conversation.start`, `conversation.cancel`, `tool.result`, `mcp.configure`
- Sidecar → Desktop: `agent.event`, `tool.invoke`, `approval.suggest`, `health.changed`
- 양방향: `initialize`, `ping`, `shutdown`

모든 요청은 `requestId`, `conversationId`, `correlationId`, `deadline`, `protocolVersion`을 가진다. 스트리밍 이벤트에는 단조 증가 `sequence`를 두어 중복과 역전을 검출한다.

Desktop의 `SidecarSupervisor`는 다음을 담당한다.

- 앱이 생성한 일회성 pipe 이름/세션 토큰으로 자식 프로세스 시작
- handshake와 계약 버전 검증
- heartbeat, timeout, 취소, 정상 종료
- crash 시 지수 backoff 재시작과 crash loop 차단
- stdout/stderr의 크기 제한 로그 수집
- 사이드카가 죽었을 때 진행 중 작업을 `Interrupted`로 확정

사이드카 패키지는 앱에 포함하고 사용자 PATH의 Node/npm에 의존하지 않는다.

## 7. 데이터와 개인정보

SQLite는 WAL 모드와 순차 migration을 사용하며 Desktop만 연결한다. 초기 논리 모델은 다음과 같다.

- `conversations`, `messages`, `agent_events`
- `memories` (`kind`, `value`, `source`, `created_at`, `expires_at`, `sensitivity`)
- `scheduled_jobs`, `job_runs`
- `tool_executions`, `approval_decisions`, `undo_entries`
- `preferences`, `capability_grants`
- `mcp_servers` (비밀 제외), `schema_migrations`

원칙:

- API key/OAuth token은 SQLite나 로그에 저장하지 않고 Windows Credential Locker/DPAPI를 사용한다.
- 기억은 모델이 임의 확정하지 않는다. `memory.remember` Tool과 사용자 확인을 거친다.
- 모든 기억에 출처와 삭제 경로를 둔다. 설정에서 조회·수정·삭제·내보내기를 제공한다.
- 화면, 클립보드, 선택 파일은 요청 시점에만 조회하고 모델 전송 전 preview/마스킹을 적용한다.
- 대화 보존 기간과 telemetry는 기본값을 명시하며 telemetry는 opt-in으로 시작한다.
- 로그에는 prompt 전문, 클립보드 전문, 파일 내용, 토큰을 기본 기록하지 않는다.

## 8. 예약 작업 모델

MVP 예약은 앱 내부 durable scheduler가 담당하고 로그인 시 앱 자동 시작으로 복구한다.

- 자연어는 Sidecar가 구조화된 `ScheduleSpec`으로 변환한다.
- Desktop이 timezone, DST, 다음 실행 시각, 반복 규칙을 검증해 사용자에게 보여준다.
- 실행 lease와 상태 전이(`Pending → Running → Succeeded/Failed/Cancelled`)를 DB transaction으로 기록한다.
- 앱이 꺼져 있던 동안 놓친 작업의 정책은 `skip`, `run_once_on_resume`, `ask` 중 명시한다.
- 위험 Tool을 예약할 때는 생성 승인과 실행 시 승인 정책을 분리한다.

Windows 로그아웃 상태나 앱 미실행 상태에서도 실행해야 하는 요구가 확인되면 후속 단계에서 Task Scheduler adapter를 추가한다. MVP에 Service를 넣지 않는다.

## 9. MCP 설계

- Sidecar는 MCP client manager만 제공하며 연결별 Tool namespace와 capability를 격리한다.
- 서버 추가 시 발견된 Tool, 위험도, 데이터 전송 대상을 사용자에게 보여주고 승인받는다.
- 원격 서버는 표준 인증 흐름과 OS 보안 저장소를 사용한다.
- MCP 서버가 제공한 Tool annotation은 신뢰하지 않고 로컬 정책으로 다시 분류한다.
- 호출 결과는 크기/콘텐츠 타입/timeout을 제한하고 prompt injection 경계로 취급한다.
- 서버 장애는 해당 연결만 격리하며 내장 Tool과 다른 MCP 연결에 전파하지 않는다.
- RAG는 MCP의 읽기 전용 capability로 시작하고 문서 쓰기/외부 공유는 별도 등급으로 둔다.

## 10. 구현 단계

각 단계는 독립적으로 데모 가능하고, 다음 단계 착수 전에 acceptance test를 자동화한다.

### Phase 0 — 결정 검증과 기반 (약 1주)

- 솔루션/워크스페이스, formatter, analyzer, 테스트, CI 구성
- ADR: WPF, 프로세스 경계, DB 단독 소유, IPC, Tool schema, 런타임 선택
- Cline agents/sdk 비교 spike와 기록된 이벤트 fixture
- JSON Schema 코드 생성과 계약 호환성 테스트
- Desktop이 sidecar를 시작하고 ping/restart/cancel하는 walking skeleton

완료 조건: 한 번의 명령으로 build/test가 되고, 가짜 에이전트 이벤트가 UI에 스트리밍되며 sidecar 강제 종료 후 UI가 멈추지 않고 복구된다.

### Phase 1 — 상주 셸과 대화 (약 1~2주)

- single-instance, tray, 시작프로그램, quick input, 설정
- 대화/진행/오류/취소 UI
- LLM provider 설정과 Credential Locker
- conversation/event persistence
- `system.get_status`, `system.show_notification`, `app.list_windows`, `app.launch`

완료 조건: 단축키 → 자연어 요청 → Tool 승인/실행 → 결과 스트리밍의 end-to-end 경로가 동작하고 모델 없는 replay E2E 테스트가 통과한다.

### Phase 2 — 안전한 Windows 작업 (약 2주)

- Tool registry, policy engine, approval UI, audit activity
- 앱 활성화/종료
- 파일 검색/열기/복사/이동/이름 변경/휴지통과 undo
- 클립보드 읽기/쓰기 및 민감 컨텍스트 표시
- 경로 정규화, overwrite/batch 제한, timeout/cancel

완료 조건: 충돌·권한 거부·긴 경로·junction·부분 실패 테스트가 있고 R2 이상은 승인 없이는 실행되지 않는다.

### Phase 3 — 기억과 예약 (약 1~2주)

- 명시적 memory CRUD와 관리 화면
- durable scheduler, 반복 규칙, 재시작 복구, 알림 action
- 별칭 및 선호 기반 Tool 인자 해석
- DB migration/backup/restore runbook

완료 조건: 재부팅/앱 재시작/DST/놓친 실행 시나리오가 테스트되고, 사용자가 모든 기억과 예약을 조회·삭제할 수 있다.

### Phase 4 — UI Automation (약 2주)

- UIA tree inspect/find/invoke/set-value
- 대상 창/element identity 재검증과 focus guard
- 제한된 send-keys, 사용자 입력 감지 시 중단
- 좌표 fallback은 별도 capability/승인으로 격리

완료 조건: 샘플 WPF/Win32 앱 fixture에서 DPI, 다중 모니터, 창 이동 후에도 안정적으로 동작하고 잘못된 창에 입력하지 않는다.

### Phase 5 — MCP와 베타 배포 (약 2주)

- stdio/Streamable HTTP 연결 관리, auth, health, timeout
- Tool namespace 충돌 처리와 로컬 재분류
- 샘플 RAG MCP read-only 연결
- MSIX/서명/자동 업데이트 전략, 진단 번들, crash recovery
- 성능/메모리/장시간 실행/보안 위협 테스트

완료 조건: 연결 하나의 장애가 앱 전체에 영향을 주지 않고, 깨끗한 Windows 사용자 계정에서 설치→로그인 자동 시작→업데이트→제거가 검증된다.

### Phase 6 — 선택 기능

- OCR/VLM 화면 분석, 브라우저 자동화, 음성
- 탐색기/선택 텍스트 통합
- opt-in 능동 제안과 방해 금지 시간
- 고급 사용자용 sandboxed shell profile

## 11. 테스트 전략

테스트 피라미드는 모델과 실제 데스크톱에 대한 의존을 최소화한다.

1. Domain unit: 정책 행렬, 스케줄 계산, 기억 규칙, 경로/위험 판정
2. Contract: 모든 JSON-RPC/Tool fixture를 C#과 TypeScript가 동일하게 검증
3. Adapter integration: 임시 SQLite, 가짜 clock/filesystem/window/MCP server
4. Windows integration: 격리된 테스트 폴더와 전용 샘플 앱만 조작
5. Replay E2E: 기록된 agent event를 재생해 UI→승인→Tool→감사 흐름 검증
6. Provider smoke: 소수의 비결정적 실모델 테스트는 수동/야간 실행
7. Soak/fault: sidecar kill, pipe 단절, DB busy, MCP hang, sleep/resume, 네트워크 변화

모든 외부 효과 port에 fake 구현을 둔다. 테스트가 실제 사용자 파일, 클립보드, 앱을 건드리려면 명시적인 integration category가 필요하다.

## 12. AI 에이전트 친화적 개발 규칙

- 저장소 루트 `AGENTS.md`에 build/test/architecture/safety 명령을 짧게 유지한다.
- 기능 변경은 해당 Tool의 schema, 정책, handler, 테스트, 사용자 문서를 함께 수정한다.
- 공개 계약은 schema-first이며 수작업으로 C#/TS DTO를 각각 편집하지 않는다.
- 새 의존성·프로세스·데이터 저장소·권한 변경은 짧은 ADR을 요구한다.
- source file은 한 책임을 유지하고 composition root 외에는 숨은 전역 상태를 금지한다.
- 시간, GUID, 파일시스템, Windows API, LLM은 port 뒤에 두어 결정적 테스트가 가능하게 한다.
- golden replay와 실패 fixture를 버그 수정마다 추가해 같은 오류를 재현 가능하게 한다.
- PR 검증은 `format → static analysis → unit → contract → integration → package smoke` 순서의 단일 스크립트로 제공한다.
- 로그/오류에는 `correlationId`를 넣되 민감 원문은 넣지 않는다.
- 문서의 각 Phase와 Tool에 기계적으로 확인 가능한 완료 조건을 둔다.

이 규칙의 목적은 AI 에이전트가 전체 시스템을 추측하지 않고도 “계약 한 개 + handler 한 개 + 관련 테스트” 범위에서 안전하게 개선하도록 만드는 것이다.

## 13. 주요 리스크와 대응

| 리스크 | 대응 |
|---|---|
| Cline 0.0.x API 변경 | adapter 격리, 정확한 pin, replay/contract gate, 수동 업그레이드 |
| LLM의 잘못된 Tool 호출 | schema 검증, allowlist, Desktop 정책, preview/승인 |
| 프롬프트 인젝션/MCP 오염 | 외부 콘텐츠 비신뢰, Tool 결과 경계, 데이터 전송 승인 |
| 잘못된 파일/창 조작 | canonical target, identity 재검증, undo, batch 제한 |
| 사이드카/IPC 장애 | supervisor, deadline/cancel, crash loop 차단, Desktop 기능 독립 |
| SQLite 손상/동시 접근 | 단일 소유자, migration transaction, backup/restore |
| 상주 앱 자원 과다 사용 | 이벤트 기반 감지, polling 최소화, idle budget와 soak test |
| UIA 호환성 차이 | UIA 우선, 앱별 adapter, 좌표/VLM fallback 격리 |
| 예약 작업 누락 | durable 상태, resume reconciliation, 명시적 misfire policy |

## 14. 최초 백로그 순서

1. 저장소/CI/테스트 뼈대와 `AGENTS.md`
2. RPC 및 `system.get_status.v1` schema
3. 가짜 Sidecar + Desktop supervisor walking skeleton
4. Cline runtime spike와 ADR
5. 트레이/quick input/대화 스트리밍
6. Tool pipeline과 policy/approval/audit
7. `app.launch`, `file.open`, `file.move`, `file.recycle`
8. SQLite 대화/감사 저장과 migration
9. 메모리 CRUD
10. durable reminder
11. UI Automation
12. MCP client와 RAG MCP 예제

Phase 0이 끝나기 전에는 화면 VLM, 음성, 브라우저 자동화, 능동 감지를 병렬로 확장하지 않는다. 먼저 계약, 정책, 장애 격리, 재현 가능한 테스트 경로를 완성한다.

## 15. 검증된 전제와 재검토 시점

- 2026-07-25 npm registry 기준 `@cline/agents`, `@cline/llms`, `@cline/sdk`의 latest는 모두 `0.0.65`다. 구현 시 lockfile과 exact version으로 고정한다.
- 현재 개발 환경에는 .NET SDK 9.0/10.0과 Node.js 24가 있다. 프로젝트는 .NET 10과 Node 24를 기준으로 시작하되 CI와 배포 번들에서 버전을 고정한다.
- 패키지 버전, Windows App SDK, MCP specification은 변할 수 있으므로 Phase 0과 각 배포 직전에 공식 소스를 다시 확인한다.
