# LIGClaw 구현 계획

> 문서 상태: living plan for v0.6.0
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

**상태: 완료 (2026-07-25).** Schema-generated 계약, 실제 Cline 0.0.65 loop를 사용하는 deterministic spike, Replay fixture, 이벤트 streaming/cancel, Sidecar 재시작과 프로세스 통합 테스트가 완료됐다. Phase 1에서는 deterministic model을 Desktop 설정·Credential Locker가 공급하는 실제 provider model로 교체한다.

- 솔루션/워크스페이스, formatter, analyzer, 테스트, CI 구성
- ADR: WPF, 프로세스 경계, DB 단독 소유, IPC, Tool schema, 런타임 선택
- Cline agents/sdk 비교 spike와 기록된 이벤트 fixture
- JSON Schema 코드 생성과 계약 호환성 테스트
- Desktop이 sidecar를 시작하고 ping/restart/cancel하는 walking skeleton

완료 조건: 한 번의 명령으로 build/test가 되고, 가짜 에이전트 이벤트가 UI에 스트리밍되며 sidecar 강제 종료 후 UI가 멈추지 않고 복구된다.

### Phase 1 — 상주 셸과 대화 (약 1~2주)

진입 전 디자인 기반(2026-07-25): 공식 LIG Defense & Aerospace CI의 Innovative Blue/Futuristic Gray를 토큰화하고, 독자적인 LIGClaw 앱·트레이 심벌, 공용 WPF 스타일, 작업 중심 메인 화면을 적용했다. 상세 사용 규칙과 자산 재생성 절차는 `docs/DESIGN_SYSTEM.md`를 따른다.

**상태: 완료 (2026-07-26).** 단일 인스턴스, 기존 창 복원, 트레이 상주, 사용자 선택형 빠른 호출, 시작프로그램, 진행/오류/취소 UI와 범용 OpenAI 호환 `Base URL/API Key/Model` 연결 및 Windows Credential Manager 저장을 완료했다. Desktop 단독 SQLite conversation/run/event persistence와 최근 다중 턴 대화 복원을 완료했다. 같은 대화는 안정적인 `conversationId`와 Sidecar runtime을 재사용하고 요청마다 고유 `runId`만 발급하며, 명시적 새 대화에서만 문맥을 분리한다. Windows 10/11은 런타임 build와 capability로 분류하며 공통 Win32 경로를 공유하고 Windows 11 전용 API만 adapter에서 선택한다. Protocol 1.5 Desktop Tool bridge와 bounded history, R0 `system.get_status.v1`, `app.list_windows.v1`, R1 `system.show_notification.v1`, 등록 앱 한정 `app.launch.v1` 실행 경로를 연결했다. Desktop 발급 run identity, 실행별 중복 방지, adapter canonical 위험도, 일반 사용자용 `이번 한 번` 승인으로 프로세스 경계를 보강했다. 기본 단축키에서 자연어 앱 실행을 요청하고 승인한 뒤 Windows 실행 결과를 모델 응답으로 스트리밍하는 replay E2E가 완료 조건을 검증한다.

- single-instance, tray, 시작프로그램, quick input, 설정
- 대화/진행/오류/취소 UI
- LLM provider 설정과 Credential Locker
- conversation/event persistence
- `system.get_status`, `system.show_notification`, `app.list_windows`, `app.launch`

다음 Phase 2 구현 순서는 `docs/HANDOFF.md`를 단일 인수인계 문서로 사용한다. 현재 R1은 매 호출마다 `이번 한 번` 승인을 요구하며 R2-R4는 동작별 preview와 제한 조건을 먼저 구현한다. Windows 10은 build profile 자동 테스트 외에 22H2 실기 acceptance가 필요하다. Cline의 미사용 Dify provider 하위 의존성에는 low 등급 npm advisory 1건이 남아 있어 호환 가능한 upstream 갱신 시 수동 재검토한다.

완료 조건: 단축키 → 자연어 요청 → Tool 승인/실행 → 결과 스트리밍의 end-to-end 경로가 동작하고 모델 없는 replay E2E 테스트가 통과한다.

### Phase 2 — 안전한 Windows 작업 (약 2주)

**상태: 완료 + 관측·문맥 확장 (2026-07-26).** Desktop Tool registry에 canonical 위험도·capability·timeout·우선순위를 선언하고 모든 실행을 run identity, 정책, preview, `이번 한 번` 승인, adapter 실행, append-only 감사 기록의 한 경로로 통합했다. 창 활성화·정상 닫기, 설치 앱 표시 이름 검색, Explorer 현재 폴더·선택 항목, 파일 검색·메타데이터·제한 텍스트 읽기·열기·복사·이동·이름 변경·Windows 휴지통·identity 기반 undo, 클립보드 텍스트 읽기·쓰기를 schema-first 계약으로 연결했다. 민감한 Explorer 경로와 파일 텍스트는 승인 뒤에만 모델로 전달하고 진단·감사 원문에는 저장하지 않는다. 파일 쓰기는 절대/canonical 경로와 reparse 최종 대상 재검증, 최대 20개, 덮어쓰기 금지, 보호/UNC 위치 차단, 항목별 부분 실패, Desktop timeout/cancel을 적용한다. 실행 활동 화면은 승인과 결과를 민감 원문 없이 보여주고 가능한 파일 작업을 동일한 R2 승인 경로로 되돌린다. 후속 감사에서 R0 `system.get_storage_status.v1`, `system.get_resource_status.v1`, `system.get_network_status.v1`을 추가해 논리 볼륨 용량, CPU·메모리·업타임, 주소 원문 없는 어댑터 상태를 모델이 직접 조회하도록 확장했다. 전체 Windows 기능 갭과 위험도별 순서는 `docs/WINDOWS_AGENT_CAPABILITY_AUDIT.md`에서 추적한다.

- Tool registry, policy engine, approval UI, audit activity
- 앱 활성화/종료
- 파일 검색/열기/복사/이동/이름 변경/휴지통과 undo
- 클립보드 읽기/쓰기 및 민감 컨텍스트 표시
- 저장소·CPU·메모리·업타임·네트워크 R0 관측
- 경로 정규화, overwrite/batch 제한, timeout/cancel

완료 조건: 충돌·권한 거부·긴 경로·junction·부분 실패 테스트가 있고 R2 이상은 승인 없이는 실행되지 않는다.

### Phase 3 — 기억과 예약 (약 1~2주)

**상태: 완료 (2026-07-26).** 명시적 개인 기억의 schema-first `remember/list/forget`, Desktop 승인 정책, SQLite upsert·출처·민감도·만료, 활성·만료 기억 CRUD·JSON 내보내기 관리 화면을 구현했다. 제한된 Windows Tool 대상 인자는 `$alias:<키>`와 `$preference:<키>`를 Desktop에서 해석하고 승인된 값을 실행까지 고정한다. schema-first `schedule.create/list/cancel`, SQLite schema 5 `scheduled_jobs`·`job_runs`, lease 상태 전이, 단발·매일·매주 현지 시각 반복, DST gap/overlap 정책, `skip`/`run_once_on_resume`/`ask`, 앱 재시작 복구와 예약 조회·수정·삭제 화면을 완료했다. R1 등록 앱 실행·동일 기억 저장·동일 예약에는 SHA-256 exact scope와 30일 만료를 가진 지속 승인 및 설정의 개별 철회를 추가했다. DB 백업·복구 runbook과 ADR 0011·0012·0016에 데이터·실행 경계를 기록했다.

- 명시적 memory CRUD와 관리 화면
- durable scheduler, 반복 규칙, 재시작 복구, 알림 action
- 별칭 및 선호 기반 Tool 인자 해석
- DB migration/backup/restore runbook

완료 조건: 재부팅/앱 재시작/DST/놓친 실행 시나리오가 테스트되고, 사용자가 모든 기억과 예약을 조회·삭제할 수 있다.

### Phase 4 — UI Automation (약 2주)

**상태: 기반 수직 슬라이스 완료 (2026-07-26).** schema-first `inspect/invoke/set-value/send-text`, Desktop 발급 5분 element handle, HWND·PID·프로세스 시작 시각·UIA runtime identity 재검증, foreground/focus guard, 비밀번호 요소 차단과 사용자 입력 감지 중단을 구현했다. WPF/native Win32 fixture에서 창 이동 뒤 요소 조회·재해석을 자동 검증한다. 좌표와 임의 단축키는 노출하지 않았다. 앱별 UIA provider 호환성 실기 acceptance는 남아 있다.

- UIA tree inspect/find/invoke/set-value
- 대상 창/element identity 재검증과 focus guard
- 제한된 send-keys, 사용자 입력 감지 시 중단
- 좌표 fallback은 별도 capability/승인으로 격리

완료 조건: 샘플 WPF/Win32 앱 fixture에서 창 이동 후에도 안정적으로 동작하고 잘못된 창에 입력하지 않는다. 별도 배율·물리 다중 모니터 검증은 필수 조건이 아니다.

### Phase 5 — MCP와 베타 배포 (약 2주)

Phase 5 착수 전 제품 작업인 **UI/UX 전면 개선 목표**는 완료했다. WPF와 기존 보안·프로세스 경계를 유지하면서 통합 앱 셸, 대화 타임라인, 관리 페이지, 승인·오류 피드백, 접근성 및 반응형 레이아웃을 개편했다. 단계와 증거는 `docs/UI_UX_REDESIGN_PLAN.md`와 `docs/UI_UX_ACCEPTANCE.md`를 따른다.

**상태: 구현 완료, 외부 서명·깨끗한 계정 release gate 대기 (2026-07-26).** Protocol 1.7의 schema-first MCP 설정·상태·호출, 공식 TypeScript SDK 기반 Streamable HTTP와 승인된 실행 ID 기반 stdio, Credential Manager Bearer 인증, 연결별 장애·health·timeout 격리를 구현했다. 발견 도구는 exact-name allowlist 전에는 비활성이며 실제 외부 호출은 Desktop canonical R3 승인·감사 pipeline을 지난다. 읽기 전용 샘플 RAG, bounded result, 진단 번들, parent/crash recovery, MCP·Desktop soak, self-contained MSIX·SHA-256 서명·App Installer 업데이트·설치 수명주기 자동화가 준비됐다. 현재 비관리자 개발 PC에서는 temporary CurrentUser root의 AppX 설치를 Deployment Service가 `0x800B0109`로 거부하므로, production/machine-trusted 인증서가 있는 깨끗한 Windows 계정에서 설치→로그인 자동 시작→업데이트→제거 runbook만 외부 release gate로 남는다. 상세 증거는 `docs/PHASE5_SECURITY_ACCEPTANCE.md`와 ADR 0015를 따른다.

- stdio/Streamable HTTP 연결 관리, auth, health, timeout
- Tool namespace 충돌 처리와 로컬 재분류
- 샘플 RAG MCP read-only 연결
- MSIX/서명/자동 업데이트 전략, 진단 번들, crash recovery
- 성능/메모리/장시간 실행/보안 위협 테스트

완료 조건: 연결 하나의 장애가 앱 전체에 영향을 주지 않고, 깨끗한 Windows 사용자 계정에서 설치→로그인 자동 시작→업데이트→제거가 검증된다.

### Phase 6 — 선택 기능

- 에어갭 전용 확장 계획은 `docs/AIRGAP_AGENT_PLAN.md`를 따른다.
- 포함: 사내망 브라우저 자동화·안전한 search/fetch, 대화 FTS·선택형 로컬 의미 기억, 예약 agent job·background task, 다중 로컬 모델 프로필·fallback, 로컬 하위 에이전트
- **완료 상태 (2026-07-26):** 에어갭 확장 A-1~A-6을 구현했다. 대화 FTS, 선택형 의미 기억, bounded Web·격리 Edge, durable Agent job, immutable 다중 모델 routing·제한 fallback, Desktop 소유 parent/child ledger와 bounded local subagent가 Protocol 1.12·SQLite schema 11까지 연결됐다. 전체 회귀·Named Pipe 통합·Sidecar/UI smoke가 모두 통과했다.
- 제외: Skills·플러그인 마켓, 외부 메시징 채널, OCR/VLM·음성. 공용 인터넷·cloud 서비스는 필수 의존성으로 두지 않지만 사용자가 구성한 도달 가능한 endpoint를 앱이 주소 대역으로 차단하지 않는다.
- 탐색기/선택 텍스트 통합
- opt-in 능동 제안과 방해 금지 시간
- 고급 사용자용 sandboxed shell profile

### Phase 7 — B단계 Windows 실무 자동화

**상태: 완료 (2026-07-26).** 상세 범위와 완료 증거는 `docs/B_STAGE_PLAN.md`를 따른다. Protocol 1.13에서 물리 디스크·BitLocker와 Defender·방화벽·Windows Update 관측, bounded 폴더·UTF-8 파일·ZIP 작업, 창 상태·Windows 설정·잠금/절전, 대화 범위 exact R1 승인, Agent 작업 일시정지·재개·재시도와 완료 알림을 연결했다. SQLite schema는 11을 유지하며 온라인 백업, 무결성 검증 복원 staging, 다음 시작 원자 교체와 운영 이력 정리를 Desktop에 추가했다.

- 고정 WMI/COM provider와 항목별 unavailable 장애 격리
- canonical/reparse 재검증, 덮어쓰기 금지, ZIP traversal·symlink 차단
- R1 exact-scope 대화 승인과 R2 매회 승인
- durable Agent 작업 제어, 완료 알림 실패와 실행 ledger 격리
- API 키를 제외한 로컬 데이터 백업·복원·보존 정책

### Phase 8 — C단계 출시 후보 안정화와 유지보수성

**상태: 진행 중 (2026-08-02).** 상세 순서와 완료 조건은 `docs/C_STAGE_PLAN.md`를 따른다. Windows PowerShell 5.1·PowerShell 7 검증 기준선, Sidecar Tool catalog, Desktop 대화 orchestration, SQLite repository와 설정 section 책임 분리를 완료했다. bounded release evidence 자동화를 추가했고 외부 Windows 10 22H2·서명 설치·실앱 UIA gate는 별도 추적한다. C-4는 Protocol 1.14 R0 전원, Protocol 1.15 R1 bounded 상위 앱 리소스, Protocol 1.16 R1 현재 네트워크 상세, Protocol 1.17 R0 비식별 장치 상태 진단까지 구현했다. C-5는 이미지/OCR preview·crop·로컬 OCR, 기본 꺼짐 누르고 말하기 STT, 선택 답변 TTS, 자동 읽기와 방해 금지 시간을 구현했다. Explorer 선택 항목은 최대 20개·32KiB의 기존 경로만 초기 실행/current-user Named Pipe로 전달해 composer preview에 추가하는 activation 기반을 구현했고, 공식 native `IExplorerCommand`와 MSIX 등록은 SDK 환경 gate다. ADR 0035의 기본 꺼짐 로컬 반복 작업 제안은 동일 완료 요청의 일간/주간 패턴만 제안하고 사용자가 기존 Agent 작업 편집기에서 확인해야 예약한다. 다음은 설치 실행·Windows 10/11 실기 acceptance와 외부 배포 gate다.

- C-0: 검증 기준선과 문서 일치
- C-1: AI 에이전트가 국소적으로 수정 가능한 책임 분리
- C-2: 재현 가능한 로컬 release candidate 자동화
- C-3: Windows 10/11, production 서명, 실제 UIA 외부 gate
- C-4: 최소 데이터 기반 일상 진단 기능
- C-5: 별도 개인정보 경계를 갖춘 화면·OCR·음성

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
| Cline의 미사용 provider 하위 의존성 advisory | 실제 노출 경로 확인, 강제 메이저 override 금지, upstream 호환 버전에서 수동 갱신 |
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
