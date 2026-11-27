# LIGClaw D단계 계획 — Cline 기반 폐쇄망 Capability Platform

> 상태: D-0~D-7 구현·인수 완료, D-8~D-10 후속 백로그
> 기준일: 2026-08-11
> 기준 커밋: `b98697b`
> 실행 상태: D-0~D-7 인수 완료 · D-6C0~2 text-only 기준 동결 · attachment 보류 · E단계 출시 안정화 진행
> 완료 증거: `D0_BASELINE.md`, `D1_ACCEPTANCE.md`, `D2_ACCEPTANCE.md`, `D3_ACCEPTANCE.md`, `D4_ACCEPTANCE.md`, `D5_ACCEPTANCE.md`, `D6A_ACCEPTANCE.md`, `D6B_ACCEPTANCE.md`, `D6C_ACCEPTANCE.md`
> 선행 문서: `IMPLEMENTATION_PLAN.md`, `AIRGAP_AGENT_PLAN.md`, `B_STAGE_PLAN.md`, `C_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0036-retain-cline-sidecar-and-desktop-owned-capability-control-plane.md`
> D-2 실행 결정: `adr/0037-frozen-capability-run-snapshot-and-selector-handshake.md`
> D-3 실행 결정: `adr/0038-desktop-owned-workflow-ledger-and-one-shot-execution-ticket.md`
> D-4 실행 결정: `adr/0039-desktop-owned-connection-profiles-and-bound-http-executor.md`
> D-5 실행 결정: `adr/0040-desktop-owned-feature-builder-proposal-pipeline.md`
> D-6 실행 결정: `adr/0041-desktop-owned-mail-draft-and-delivery-semantics.md`, `adr/0042-fixed-mail-gateway-and-durable-submission-journal.md`, `adr/0043-typed-smtp-fallback-and-data-outcome-ambiguity.md`
> 제품 전제: Windows 10/11 사용자 세션, 사내 vLLM, 폐쇄망·사내망, 번들 Node.js Sidecar

## 1. 결론

LIGClaw의 다음 목표는 Sidecar를 제거하는 것이 아니다. 현재 검증된 프로세스 경계를 유지하면서 **Cline SDK 기반 Agent Runtime 위에 버전형 Capability, 선언형 Workflow, Connection Profile, Feature Builder를 추가**한다.

목표 구조는 다음과 같다.

```text
.NET 10 WPF Desktop
  +
Desktop-owned Capability Control Plane
  +
Node.js Sidecar / @cline/agents Agent Runtime
  +
Schema-first Tool · Workflow Runtime
  +
SQLite · Credential Manager
  +
사내 vLLM · 승인된 사내 시스템
```

제품에서 사용자가 관리하는 주 애플리케이션은 계속 `LIGClaw.Desktop.exe` 하나다. Node.js Sidecar는 앱에 포함된 자식 프로세스로 유지하며 사용자가 Node.js나 npm을 별도로 설치하지 않는다.

다음 기존 결정은 변경하지 않는다.

1. Desktop은 UI, Windows 효과, 승인, 정책, SQLite, 예약, 비밀, 감사를 소유한다.
2. Sidecar는 Cline Agent loop, 모델 연결, prompt 조립, Tool proxy, MCP session을 소유한다.
3. Sidecar와 모델은 Desktop 정책을 우회해 Windows·파일·외부 시스템을 실행할 수 없다.
4. Tool과 IPC payload는 `contracts/`의 JSON Schema를 단일 원천으로 사용한다.
5. Cline 패키지는 adapter 뒤에 격리하고 exact version으로 고정한다.
6. 새 기능은 작은 수직 슬라이스와 deterministic replay test로 추가한다.

## 2. 이전 통합 설계서의 판정

### 유지할 방향

- 사내 vLLM과 OpenAI 호환 endpoint
- 대화로 기능을 추가·수정하는 Capability Studio
- Prompt, Workflow, 설정, 테스트의 버전 관리
- Connection Profile과 Credential Manager secret 분리
- 앱 재시작 없는 기능 활성화와 이전 버전 rollback
- 이메일 기능을 첫 번째 완전한 기준 구현으로 사용
- 사내 Git을 통한 Capability 공유
- 실패 결과를 이용한 개선 제안
- 실행 이력과 기능별 통계
- 필요 시 Remote Runner를 별도 후속 제품으로 추가

### 폐기하거나 수정할 전제

| 이전 전제 | 수정 결정 |
|---|---|
| Sidecar와 Node.js 제거 | Cline SDK가 Node.js/TypeScript용이므로 Sidecar 유지 |
| Cline SDK를 .NET에 통합 | 불가능한 조합이므로 시도하지 않음 |
| IPC Schema 제거 | Desktop–Sidecar 경계와 Tool drift 방지를 위해 유지 |
| WPF를 Blazor Hybrid Dashboard로 즉시 교체 | 현재 검증된 WPF Precision Workspace를 확장 |
| 범용 PowerShell·cmd를 첫 Executor로 제공 | typed Tool과 선언형 Workflow를 먼저 제공 |
| 모든 승인 비활성화 가능 | exact-scope grant와 unattended 정책으로 제한 |
| C# DLL을 메인 프로세스에 즉시 hot-load | 마지막 고급 단계로 분리하고 별도 worker 우선 검토 |
| AI가 활성 기능을 직접 수정 | proposal → 검사 → 시험 → 승인 → 활성화 절차 사용 |
| Capability 변경을 실행 중 session에 즉시 주입 | 현재 run은 immutable snapshot 유지, 다음 run부터 적용 |

## 3. 현재 구현 기준선

현재 LIGClaw에는 다음 기반이 이미 있다.

- `@cline/agents`, `@cline/llms`, `@cline/shared` `0.0.65` 기반 Agent Runtime
- OpenAI 호환 model profile, per-run immutable routing, bounded fallback
- schema-first Desktop Tool catalog와 Cline custom `AgentTool`
- native Tool Calling과 제한된 JSON text fallback
- Desktop-issued `conversationId`, `runId`, at-most-once `toolCallId`
- R0~R4 위험도, preview, 승인, exact-scope 지속 grant, 감사
- 진입 기준 Desktop 단독 SQLite schema 11과 repository 분리, D-1 current schema 13
- D-6C0~2 구현 기준 Desktop SQLite current schema 20과 Protocol 1.23 유지
- durable 알림, Agent job, task lease, 재시도, misfire, 복구
- local subagent ledger와 권한 상한
- MCP Streamable HTTP, fixed-registry stdio, exact tool allowlist
- Windows Tool, 파일, UIA, Web, 브라우저, 기억, 예약, 화면·OCR·음성
- WPF Precision Workspace, 관리 페이지, Action Card, UI smoke
- Sidecar contract/replay/process test와 release evidence 자동화

D단계는 위 기능을 대체하지 않고 일반화한다. 특히 Capability는 기존 Tool을 숨겨서 직접 실행하는 새로운 우회 경로가 아니라 **기존 Tool 정책을 조합하는 상위 계층**이다.

D-0 기준선부터 D-5 Feature Builder까지의 단계별 구현과 검증 증거는 `D0_BASELINE.md`, `D1_ACCEPTANCE.md`, `D2_ACCEPTANCE.md`, `D3_ACCEPTANCE.md`, `D4_ACCEPTANCE.md`, `D5_ACCEPTANCE.md`를 따른다.

## 4. 제품 목표

사용자는 자연어로 사내 기능을 만들고 다음 수명주기를 관리할 수 있어야 한다.

```text
요청
 → 필요한 정보 수집
 → Capability 초안 생성
 → 정적 검사와 readiness 확인
 → dry-run / 연결 시험
 → diff와 영향 범위 검토
 → 사용자 승인
 → 버전 활성화
 → 실행과 감사
 → 실패 분석
 → 수정 제안
 → 새 버전 승인 또는 rollback
```

대표 요청은 다음과 같다.

- 사내 메일 API로 이메일 보내기
- 사내 Git 저장소의 주간 변경사항 요약
- Confluence 또는 사내 문서 검색
- SQL Server 읽기 전용 조회와 통계 생성
- Excel 보고서 생성
- 승인된 사내 프로그램의 UI Automation
- 예약된 상태 점검과 보고서 초안 작성

핵심 성공 기준은 기능 수가 아니라 다음이다.

1. 새 기능이 본체 재빌드 없이 추가된다.
2. 활성화 전에 필요한 설정과 실행 범위를 확인할 수 있다.
3. 실행 시 기존 Desktop 승인·감사 정책을 그대로 통과한다.
4. 실패한 기능은 활성 버전을 손상시키지 않고 새 draft로 수정된다.
5. Sidecar crash, 앱 재시작, 모델 장애 후에도 실행 상태가 결정적으로 정리된다.
6. 공용 인터넷과 외부 SaaS 없이 build, test, import, 실행이 가능하다.

## 5. 목표 아키텍처

```text
┌──────────────────────── LIGClaw Desktop (.NET 10 / WPF) ───────────────────────┐
│ UI                                                                            │
│ Chat · Capabilities · Builder · Connections · Automations · Tasks · History  │
│                                                                               │
│ Application / Control Plane                                                   │
│ Capability Catalog · Draft/Review · Readiness · Activation · Workflow Runtime│
│ Tool Policy · Approval · Scheduler · Task Ledger · Audit                     │
│                                                                               │
│ Infrastructure                                                               │
│ SQLite · Credential Manager · Windows APIs · HTTP/SMTP/SQL adapters          │
│                                                                               │
│ SidecarSupervisor ── framed JSON-RPC / per-launch Named Pipe ─────────────┐   │
└───────────────────────────────────────────────────────────────────────────┼───┘
                                                                            │
┌──────────────────────── Bundled Node.js Sidecar ───────────────────────────┴───┐
│ ClineAgentRuntimeAdapter                                                      │
│ @cline/agents · @cline/llms · custom AgentTool · hooks · prepareTurn          │
│ Capability Runtime Projection · Builder Agent                                 │
│ Desktop Tool Bridge · MCP Client Manager · Provider Routing                   │
└──────────────────────────────────┬─────────────────────────────────────────────┘
                                   │
                  ┌────────────────┴────────────────┐
                  │ 사내 vLLM · 승인된 MCP endpoint │
                  └─────────────────────────────────┘
```

### 5.1 Desktop 책임

- Capability, version, draft, activation, rollback의 authoritative store
- Capability package import/export와 provenance/hash
- Connection Profile metadata와 secret
- 사용자·관리자 설정과 실행 정책
- candidate 검색의 로컬 FTS 1차 필터
- Workflow definition 정적 검증, validated plan, 해석·실행, per-step one-shot 승인과 복구 판정
- durable task/run/step 상태와 감사
- 모든 Windows·파일·HTTP·SMTP·SQL·process 실제 실행
- 예약과 unattended 실행 가능 여부 결정
- UI와 승인 결정

### 5.2 Sidecar 책임

- Cline Agent Runtime과 대화 session
- vLLM 호출, streaming, cancellation, model fallback
- Desktop이 전달한 bounded Capability snapshot의 prompt 조립
- bounded candidate 중 최종 Capability 선택
- manifest에서 Cline `AgentTool` projection 생성
- Workflow Capability를 schema-first Desktop runtime에 요청하고 결과를 Cline에 재투입
- MCP session과 discovered tool projection
- 실행 이벤트를 normalized `AgentEvent`로 전달

### 5.3 금지되는 소유권

- Sidecar는 SQLite, Capability 파일, activation 상태를 직접 쓰지 않는다.
- Sidecar는 Credential Manager secret을 장기 저장하지 않는다.
- Capability prompt나 model이 canonical risk를 정하지 않는다.
- Workflow는 Desktop Tool pipeline을 건너뛰지 않는다.
- Desktop은 Cline SDK type을 참조하지 않는다.
- Domain과 Application은 WPF, SQLite, Cline, MCP 구현을 참조하지 않는다.

## 6. Cline SDK 호환 설계

### 6.1 고정 전제

- `@cline/agents`, `@cline/llms`, `@cline/shared`는 현재처럼 `0.0.65` exact pin을 유지한다.
- `@cline/agents`는 Node.js 패키지이므로 Sidecar 안에서만 사용한다.
- 번들 Node.js 24를 사용하며 사용자 PATH의 Node/npm에 의존하지 않는다.
- `@cline/core`의 기본 filesystem, shell, session persistence를 가져오지 않는다.
- LIGClaw가 custom Tool, persistence, subagent, MCP policy를 계속 소유한다.

현재 runtime의 bounded 기준인 conversation session 최대 24개, history 최대 40개 message, agent iteration 최대 16회와 승인 대기 제한을 회귀 기준으로 유지한다. Capability와 Workflow가 이 상한을 암묵적으로 늘리지 않으며 별도의 step, depth, output, concurrency 제한을 추가한다.

### 6.2 사용 가능한 SDK 표면

현재 고정 버전에서 확인된 표면만 사용한다.

- `AgentRuntime` / `Agent`
- custom `AgentTool`과 JSON input schema
- streaming event subscription
- `run`, `abort`, `restore`, `snapshot`
- lifecycle hooks
- `prepareTurn`
- provider form의 `baseUrl`, `apiKey`, `modelId`

Capability 기능은 위 표면 밖의 Cline 내부 구현에 의존하지 않는다.

참고 기준은 배포에 잠긴 local package type/README와 [Cline 공식 저장소](https://github.com/cline/cline)다. upstream 최신 기능을 현재 pin에서 사용할 수 있다고 가정하지 않는다.

### 6.3 Capability projection

Desktop은 매 run 전에 사용자 요청과 FTS로 최대 8개의 후보를 고르고 immutable `CapabilityRuntimeSnapshot`을 Sidecar에 전달한다.

목표 Snapshot에는 단계별 contract가 허용하는 다음 정보만 포함한다. D-2 contract는 `prompt` kind와 secret-free metadata만 허용하며 Workflow·Connection projection은 해당 단계의 protocol에서 별도로 연다.

- capability ID와 exact version
- display name, description, intent examples
- bounded instructions
- input schema
- 사용할 수 있는 canonical Tool 이름
- 선언형 Workflow projection
- 필요한 Connection ID의 비밀 없는 metadata
- 최대 위험도와 catalog revision/hash

API key, password, token, 사용자 파일 원문은 포함하지 않는다.

Sidecar는 후보별 custom `AgentTool`을 생성하거나 prompt-only Capability를 `AgentRuntimeConfig.prepareTurn`으로 현재 provider request에만 투영한다. Workflow Tool의 실행 함수는 직접 step을 해석하거나 effect를 만들지 않고 schema-first `workflow.invoke`를 Desktop에 요청한다. Desktop Workflow Runtime이 exact definition을 다시 읽고 모든 step을 기존 Tool 정책 경로로 실행한 뒤 구조화 결과를 Sidecar에 반환한다.

모델에 노출하는 후보는 최대 8개로 제한해 Tool과 prompt 폭증을 막는다. model-facing 이름은 원본 ID를 그대로 조합하지 않고 snapshot 안에서 충돌이 없는 `cap_<id-hash>` 형식으로 만든다. Sidecar와 Desktop은 이 이름을 exact capability ID, version, content hash에 매핑하며 model이 임의 ID나 version을 제출해 범위를 넓힐 수 없게 한다.

### 6.4 session과 Hot Reload

Cline `AgentRuntime`의 Tool과 plugin 목록은 생성 시점에 고정된다. 따라서 실행 중 runtime의 Tool 목록을 변형하지 않는다.

```text
현재 run 시작
 → provider routing hash + capability snapshot hash 고정
 → run 종료까지 같은 snapshot 사용
 → catalog 변경 시 기존 session retire 표시
 → 다음 run에 bounded history를 restore한 새 AgentRuntime 생성
```

같은 snapshot과 bounded Desktop history가 있으면 conversation session을 restore해 재사용할 수 있다. 후보가 있는데 Desktop history가 없으면 이전 selector 결과가 남지 않도록 새 AgentRuntime을 만든다. snapshot hash가 달라지면 현재 `configureMcpTools`와 provider routing 변경 때처럼 안전하게 retire/recreate한다. 활성 run에 새 Capability를 중간 삽입하지 않는다.

### 6.5 업그레이드 정책

Cline package 업그레이드는 기능 개발과 분리한다.

- 자동 major/minor 업데이트 금지
- exact lockfile 유지
- API surface compile fixture
- real Cline loop deterministic model test
- native Tool, fallback, restore, cancellation, hooks replay
- Sidecar process handshake와 crash recovery
- vLLM provider smoke
- 전체 통과 뒤 수동 승인

## 7. Capability 모델

### 7.1 Capability 유형

D단계 초기 유형은 다음 세 가지다.

| 유형 | 역할 | 실행 코드 |
|---|---|---|
| Prompt | 기존 Tool을 올바르게 조합하는 지침 | 없음 |
| Workflow | 정해진 Tool step, 조건, 변환 | 선언형 JSON |
| Connector | Connection Profile과 typed executor 결합 | 본체의 등록 adapter |

후속 유형은 `Composite`, `ScriptProfile`, `Extension`이다. Script와 Extension은 초기 MVP에 포함하지 않는다.

### 7.2 `.ligcap` package

```text
company.mail.send-1.0.0.ligcap
├─ manifest.json
├─ instructions.md
├─ input.schema.json
├─ settings.schema.json
├─ workflow.json
└─ tests/
   ├─ readiness.json
   ├─ dry-run.json
   └─ missing-token.json
```

초기 package에는 executable, DLL, symlink, 임의 archive 중첩을 허용하지 않는다.

`manifest.json`의 핵심 필드는 다음과 같다.

```json
{
  "schemaVersion": 1,
  "id": "company.mail.send",
  "version": "1.0.0",
  "displayName": "사내 메일 전송",
  "description": "등록된 사내 메일 API로 메일을 전송합니다.",
  "kind": "workflow",
  "intentExamples": ["메일 보내줘", "보고서를 팀장에게 전달해줘"],
  "requiredTools": ["mail.create_draft.v1", "mail.send.v1"],
  "requiredConnections": [{ "slot": "mail", "kind": "mail-api" }],
  "maximumRisk": "R3",
  "entrypoint": "workflow.json"
}
```

manifest의 위험도는 설명용 상한이다. 실제 risk는 Desktop adapter registry와 compiled execution plan이 다시 판정한다.

### 7.3 버전 상태

```text
draft
 → validating
 → ready_for_review
 → active
 → superseded
 → rolled_back
```

제안 상태는 별도로 둔다.

```text
pending · rejected · quarantined · stale · applied
```

활성 버전은 수정하지 않는다. 변경은 항상 parent version을 가진 새 draft를 만든다. 제안이 만들어진 뒤 parent hash가 바뀌면 `stale`로 처리한다.

### 7.4 저장 위치

v1은 Desktop SQLite를 Capability의 단일 원본으로 사용한다.

- SQLite version row: canonical manifest, instructions, input/settings schema, workflow, tests, content hash
- SQLite catalog row: state, active version, provenance, readiness, run statistics
- Credential Manager: secret
- 같은 Desktop SQLite의 감사 table: activation, rollback, import, execution 결과

`.ligcap`은 import/export 전송 형식일 뿐 활성 저장소가 아니다. import는 임시 staging에서 size, path, schema, hash, dependency를 검증한 뒤 하나의 DB transaction으로 immutable version row를 만든다. package 원문을 별도 active 파일 트리와 이중 관리하지 않는다. 향후 대용량 asset 때문에 content-addressed file store가 필요해지면 backup/restore와 reconciliation을 포함한 별도 ADR을 먼저 작성한다.

## 8. Capability lifecycle

### 8.1 생성

Feature Builder는 사용자에게 한 번에 1~3개의 질문만 한다. 일반 입력은 `FeatureBuilderSession`에 저장하되 token과 password는 대화로 받지 않고 전용 masked secret 입력 화면을 연다. secret은 Credential Manager entry로 분리하고 transcript, proposal, test fixture에는 placeholder만 둔다.

```text
요구 파악
 → 기존 Tool/Connection으로 가능한지 확인
 → missing information 질문
 → draft manifest/workflow/tests 생성
 → Desktop proposal store에 제출
```

Sidecar는 파일이나 DB를 직접 수정하지 않고 schema-first proposal RPC를 통해 draft를 제출한다.

### 8.2 정적 검사

- ID, version, schema 검증
- package path traversal, symlink, 크기·파일 수 제한
- 존재하지 않는 Tool과 Connection kind 거부
- workflow cycle과 무제한 loop 거부
- 최대 step, input, output, timeout 검사
- 선언 risk와 실제 Tool risk의 불일치 검사
- secret literal 검사
- prompt에 credential, 사용자 원문, 임의 shell 요구가 포함됐는지 검사

### 8.3 readiness

정적 inventory와 실제 readiness를 분리한다.

- 필요한 Connection Profile 존재
- secret 존재 여부만 확인하고 값은 노출하지 않음
- executable/프로그램/Windows capability 존재
- MCP tool discovered/allowlisted 여부
- endpoint probe
- DB read-only 연결
- test fixture 결과

### 8.4 검토와 활성화

검토 화면은 다음을 보여준다.

- 이전 버전과 manifest/instructions/workflow diff
- 필요한 Connection과 데이터 전송 대상
- 사용할 Tool과 최대 위험도
- 자동 실행 가능 여부
- dry-run과 test 결과
- 알려진 제한

사용자가 승인하면 Desktop이 version hash와 activation record를 transaction으로 저장한다. 실패하면 이전 active version을 유지한다.

### 8.5 수정과 개선

실패·사용자 교정·반복 지시로부터 개선안을 만들 수 있지만 자동 적용하지 않는다.

```text
실행 실패 또는 사용자 교정
 → bounded evidence 수집
 → 새 proposal 생성
 → 기존 버전 diff
 → test
 → 사용자 승인
 → activation 또는 rejection
```

prompt, workflow, script 원문이나 secret을 진단 로그에 기록하지 않는다.

## 9. Workflow Runtime

### 9.1 v1 step

```text
input.map
tool.call
condition
transform
needs_information
output
```

초기에는 임의 코드, `eval`, 무제한 반복, 재귀 Capability 호출을 허용하지 않는다. 변환은 JSON Pointer, 제한된 template, allowlisted 문자열·날짜·숫자 함수만 사용한다.

기본 상한 예시는 다음과 같다.

- 최대 20 step
- 최대 분기 깊이 5
- Desktop Workflow deadline 8분 45초, bridge 10분, AgentTool 11분
- step 입력 32 KiB
- step 출력 64 KiB
- final result exact 100 KiB/depth 16/node 4,096, outer envelope만 32 KiB/depth 1/node 64 추가 여유
- 재시도 0~3회
- 병렬 step은 초기 비활성

### 9.2 실행 흐름

```text
Sidecar가 Capability 선택
 → Sidecar가 capability ID/version/hash와 입력을 workflow.invoke로 요청
 → Desktop이 SQLite의 exact immutable version과 snapshot authorization 재검증
 → Desktop Workflow Runtime이 입력 schema와 실행 계획 검증
 → effect step 실행 직전에 immutable preview와 AllowOnce 승인 생성
 → Desktop이 순서·조건·변환 step 진행
 → effectful step마다 기존 ToolInvocationCoordinator 호출
 → ticket·step·payload 재검증
 → 결과와 step event 저장
 → bounded Workflow 결과를 workflow.result로 반환
 → Cline이 최종 답변 생성
```

Sidecar는 Workflow definition이나 step payload를 authoritative 값으로 보내지 않는다. Desktop이 exact version에서 definition을 다시 읽고 Tool 이름, risk, Connection과 scope를 계산하므로 model이나 stale Sidecar가 Workflow를 변조할 수 없다.

`workflow.invoke`는 `WindowsToolHost` 안의 또 다른 Tool adapter로 구현하지 않는다. 그렇게 하면 바깥 Tool 승인을 기다리는 동안 내부 step이 같은 직렬 coordinator로 재진입할 수 있다. 별도 Desktop `WorkflowOrchestrator`가 control-plane 요청을 받고, 각 effect step만 기존 Tool invocation application port로 순서대로 제출한다.

### 9.3 per-step one-shot execution ticket

D-3는 plan-wide batch 승인을 만들지 않는다. 실행할 effect step의 branch와 이전 step 결과가 확정된 뒤 Desktop이 immutable `IPreparedWorkflowEffect`를 만들고 `AllowOnce` 승인을 받은 다음, 그 effect 하나에 single-item `WorkflowAuthorizationTicket`을 발급한다.

Ticket은 다음에 결합된다.

- conversation ID, run ID
- capability ID와 exact version
- workflow hash
- normalized input hash
- exact snapshot/model Tool/capability ID·version·revision·content hash
- workflow/input/plan hash와 actual maximum risk
- step sequence, canonical Tool/risk, payload/binding hash
- prepared effect와 ticket의 만료 시각

Ticket은 Desktop만 발급·보관하며 random nonce hash와 sequence를 한 번만 소비한다. Sidecar나 재시도 요청이 이미 소비된 승인을 재사용할 수 없다. 만료는 호출자 시간이 아니라 repository에 주입된 trusted `TimeProvider`로 판정한다.

step `planned → executing` reservation과 ticket item `ready → consumed` CAS가 같은 짧은 transaction에서 함께 성공한 뒤에만 `ExecuteOnceAsync`를 호출한다. 승인 후 확정된 step, 대상, 인자, 순서나 hash가 바뀌면 policy와 effect를 실행하기 전에 거부한다.

### 9.4 crash와 재시도

Desktop은 workflow run/step ledger를 저장한다. 실행 중 exact duplicate는 같은 in-memory task에 합류하고 terminal 결과와 bounded `needs_information` pointer만 durable replay한다.

- 같은 process에서는 `read_only` observation만 정책에 따라 bounded retry
- Sidecar disconnect는 active parent CTS를 즉시 취소하고 pending bridge promise와 late result를 거부
- effect 전 취소는 `cancelled/retry_from_start`, 시작됐거나 결과가 미확정인 effect는 `outcome_unknown`/`attention_required/review_outcome`
- startup은 active ticket을 revoke하고 실행 중 observation을 `interrupted`, 시작한 effect를 `outcome_unknown`으로 복구
- v1은 restart 뒤 same-run resume나 effect 자동 재실행을 지원하지 않음. 사용자는 결과를 검토한 뒤 새 run과 새 승인을 시작

## 10. Connection Profile과 Executor

### 10.1 원칙

Connection Profile은 Capability와 secret을 분리한다.

- metadata와 상태는 Desktop 저장
- token/password는 Credential Manager
- Sidecar와 model에는 opaque Connection ID와 비밀 없는 설명만 전달
- 실제 request에는 Desktop이 secret을 주입
- Capability export에 secret을 포함하지 않음
- endpoint, account, database가 바뀌면 readiness와 grant 무효화

공유 package는 `requiredConnections`의 logical slot과 kind만 선언한다. 실제 PC의 `ConnectionProfileId`는 export되지 않는 로컬 `CapabilityConnectionBinding`에 저장한다.

```text
Capability version: slot=mail, kind=mail-api
Local binding: company.mail.send / mail → connection.mail-api-prod
Credential: connection.mail-api-prod/token → Credential Manager
```

binding 변경은 Capability version 변경이 아니다. 대신 readiness와 runtime snapshot hash를 다시 계산하고 이전 Connection target에 결합된 grant를 무효화한다.

현재 Desktop 설정 UI의 단일 `Knowledge` MCP 연결과 Sidecar manager의 최대 8개 연결 지원 차이는 Connection v1에서 함께 정리한다. UI와 Desktop metadata store를 다중 profile로 일반화하되 MCP session 소유권은 계속 Sidecar에 둔다.

### 10.2 구현 순서

1. `http` — fixed base URL, method/path template, redirect 기본 차단, bounded body/result
2. `mail-api` — fixed Gateway contract, draft와 send 분리, durable submission journal
3. `sql-read` — parameterized read-only query, read-only DB account와 statement 검증, row/column/result 상한
4. `smtp` — D-6C의 typed fallback, DATA 이후 outcome-unknown은 자동 재전송 금지
5. `git` — 등록 repository와 allowlisted operation
6. `mcp` — 기존 exact connection/tool allowlist 재사용
7. `uia` — 기존 window/element identity와 approval 재사용

### 10.3 초기 typed Executor

```text
connection.http.request.v1
connection.sql.query.v1
mail.create_draft.v1
mail.send.v1
git.status.v1
git.log.v1
git.diff.v1
```

모델이 URL, SQL 원문, executable, secret을 자유 문자열로 넘기지 않는다. Capability version이 endpoint path template, parameter schema, query template, operation을 고정하고 모델은 schema에 정의된 값만 제공한다.

첫 `connection.http.request.v1`은 GET-only canonical R3다. POST/body/header로 확장하지 않으며 mail submit은 `mail.send.v1`의 고정 Gateway adapter만 소유한다. redirect는 기본 비활성화하며, 후속으로 필요할 때만 manifest에 고정된 hop 수 안에서 매 hop의 scheme, host, port를 Connection Profile과 다시 비교한다. 읽기 전용 HTTP를 낮은 위험도로 분리하려면 별도 canonical Tool과 별도 adapter를 추가한다.

### 10.4 고급 command profile

PowerShell/CLI가 실제 업무에서 필요하다는 증거가 쌓이면 `RegisteredCommandProfile`을 별도 단계에서 추가한다.

- 관리자가 고른 executable과 SHA-256
- 고정 script package와 version
- 인자 JSON Schema
- 허용 working directory root
- 환경변수 allowlist
- timeout, output, process tree 상한
- network 필요 여부
- preview와 R3/R4 승인
- 실행 파일/hash 변경 시 profile 비활성화

Sidecar나 Capability prompt가 임의 command line을 직접 만들고 실행하는 `shell.execute`는 초기 범위에 포함하지 않는다.

## 11. 승인과 자동 실행

Capability 승인은 기존 Tool 승인을 대체하지 않는다. 활성화 승인은 “이 정의를 사용할 수 있음”이고 실행 승인은 “이 effect를 지금 수행함”이다.

지원 정책은 다음과 같다.

| 정책 | 의미 |
|---|---|
| Interactive | 기존 Tool 위험도에 따라 매번 또는 필요 시 승인 |
| Session exact | 현재 대화에서 같은 canonical scope만 허용 |
| Expiring exact | grantable R1 exact scope를 최대 30일 허용 |
| Unattended eligible | R0와 사전 승인된 exact R1만 무인 실행 |

R2~R4, 외부 전송, 파일 내용, UIA, MCP는 기존 제한을 유지한다. `모든 기능 승인 없이 실행` 같은 전역 우회는 제공하지 않는다.

지속 grant는 canonical Tool scope뿐 아니라 capability ID, exact version, content hash, Connection target, normalized effect input에 결합한다. Capability나 Connection이 갱신되면 이전 grant를 새 동작에 승계하지 않는다.

예약 작업은 승인 UI를 무기한 기다리지 않는다.

- 사전 승인 범위 안이면 실행
- 새 승인이 필요하면 `approval_required`로 fail-closed
- Tasks/Attention 화면에서 사용자가 검토 후 재실행
- 예약 생성 승인이 미래 effect 권한으로 암묵 승계되지 않음

## 12. IPC와 데이터 계약

### 12.1 계약 상태

D-2 Protocol 1.18은 `conversation.start`의 bounded `capabilitySnapshot`과 exact selector handshake를 구현했다. D-3 Protocol 1.19는 `workflow.invoke`, `workflow.result`, `workflow.runtime.v1` handshake와 `workflow.invoke.v1` schema를 구현했다.

후속 수직 slice에서는 다음 계약만 필요 시 추가한다.

- `capability.proposal.generate`
- `capability.proposal.result`
- `connection.probe`

계약은 한 번에 모두 추가하지 않고 수직 기능에 필요한 최소 payload만 추가한다.

Desktop port도 하나의 거대한 Sidecar facade로 확장하지 않는다. 대화 실행, MCP session, Tool result, Capability projection을 `IAgentRuntimeClient`, `IMcpSessionClient`, `IToolResultSink`, `ICapabilityRuntimeClient`처럼 좁은 책임으로 분리하고 composition root에서 조립한다.

### 12.2 공통 필드

- schema/protocol version
- capability ID와 exact version
- catalog revision과 snapshot hash
- conversation ID, run ID, task ID
- deadline, sequence, idempotency key
- bounded input/output
- fixed error code

### 12.3 새 SQLite 논리 모델

schema 13은 Capability Catalog를, schema 14는 Desktop-owned Workflow ledger를 추가했다. Workflow의 구현 table은 다음과 같다.

```text
workflow_runs
workflow_authorization_tickets
workflow_authorization_ticket_items
workflow_step_runs
```

D-4 이후에는 수직 slice마다 필요한 table만 별도 migration으로 추가한다.

```text
connection_profiles
capability_connection_bindings
capability_test_runs
improvement_proposals
```

대화 `Session`, background `Task`, 실행 시점 `Schedule`은 별도 개념으로 유지한다. 일반 채팅 turn을 모두 Task로 만들지 않는다.

## 13. Operations Workspace UI

OpenClaw의 Gateway나 웹 UI를 복제하지 않는다. 다음 운영 패턴만 현재 WPF Precision Workspace에 적용한다.

```text
ASSIST
 ├─ Chat
 └─ Sessions

BUILD
 ├─ Capabilities
 ├─ Feature Builder
 └─ Connections

OPERATE
 ├─ Automations
 ├─ Running Tasks
 ├─ History
 └─ Approvals

MANAGE
 ├─ Models
 ├─ Memory
 ├─ MCP
 └─ Settings / Diagnostics
```

우선 적용할 UX는 다음과 같다.

- 실패한 예약, 필요한 Connection, stale proposal을 보여주는 attention chip
- parent conversation과 child task/subagent 연결
- 실행 중 task rail, 현재 step, 취소
- Capability readiness, active version, source, hash, rollback
- proposal diff와 test 결과
- session별 model override
- Tool/Workflow 실행 카드
- 설정 검색과 영역별 navigation

OpenClaw Skill Workshop의 `proposal → review → apply` 방식, Control UI의 Session/Task/Cron 분리, revision에 결합된 Dashboard grant를 참고한다.

- [OpenClaw Skills](https://docs.openclaw.ai/tools/skills)
- [OpenClaw Skill Workshop](https://docs.openclaw.ai/tools/skill-workshop)
- [OpenClaw Control UI](https://docs.openclaw.ai/web/control-ui)
- [OpenClaw Automation](https://docs.openclaw.ai/automation)
- [OpenClaw Session Dashboards](https://docs.openclaw.ai/web/dashboards)
- [OpenClaw Security](https://docs.openclaw.ai/gateway/security)

초기에는 arbitrary HTML/JavaScript widget, WebView dashboard, public marketplace, messaging channel, device pairing을 추가하지 않는다. Dashboard가 필요해지면 schema가 고정된 native WPF 업무 카드부터 시작한다.

## 14. 단계별 구현 목표

### D-0 — 결정 고정과 기준선

작업:

1. Sidecar/Cline 유지 결정을 ADR로 기록한다.
2. Capability가 기존 Tool pipeline을 우회하지 않는다는 경계를 기록한다.
3. 현재 provider, Tool, MCP, job, subagent replay fixture를 기준선으로 고정한다.
4. `routing_changed`가 RPC schema와 Sidecar에는 있으나 `SidecarSupervisor` event allowlist에는 빠진 protocol drift를 수정하고 fallback Named Pipe replay를 추가한다.
5. initialize handshake의 advertised capability가 Desktop의 required capability와 실제로 비교되는 negotiation test를 추가한다.
6. C단계 외부 Windows 10/11·서명·UIA gate를 별도 추적한다.

기준선 fixture와 외부 release gate의 소유권·증거 형식은 `D0_BASELINE.md`에서 추적한다.

완료 조건:

- 신규 사용자 기능 추가 없음; 명시한 protocol bug와 capability negotiation만 수정
- Cline API surface fixture와 current Sidecar replay 통과
- model fallback의 `routing_changed → run_started → text/terminal` sequence 보존
- D단계 용어와 소유권이 기존 문서와 충돌하지 않음

### D-1 — Capability Catalog와 package

작업:

- Domain/Application model과 repository port
- immutable version, draft, activation, rollback
- `.ligcap` import/export와 staging validation
- FTS candidate index
- 최소 Capabilities 관리 화면

완료 조건:

- prompt-only sample Capability를 import, 검토, 활성화, rollback 가능
- app restart 후 active version 복원
- 손상, 중복 ID, path traversal, schema mismatch package 거부
- secret이 package, SQLite, 로그에 없음

### D-2 — Cline Capability Runtime Projection

상태: 완료. 상세 증거와 최신 검증 수치는 `D2_ACCEPTANCE.md`를 따른다.

작업:

- schema-first `CapabilityRuntimeSnapshot`
- Desktop FTS 후보 최대 8개
- Sidecar custom `AgentTool` projection
- `prepareTurn` prompt projection
- capability snapshot hash를 runtime session key에 포함
- catalog 변경 시 safe retire/recreate

완료 조건:

- Cline deterministic model이 후보 Capability를 선택하고 기존 Desktop Tool 실행
- 비후보 Capability, 비허용 Tool, stale version 거부
- 실행 중 activation 변경이 현재 run에 영향 없음
- 다음 run은 새 version과 bounded history 사용

### D-3 — Workflow Runtime과 execution ticket

상태: Desktop-owned runtime, Protocol 1.19·SQLite schema 14, 전체 verify와 Activity UIA smoke를 완료했다. 상세 증거는 `D3_ACCEPTANCE.md`를 따른다.

진입 원칙이었던 Workflow kind 일괄 gate는 ADR 0038의 Desktop-owned interpreter, durable step reservation, per-step one-shot prepared effect, `outcome_unknown` 복구와 선택 후 model-facing 일반 Tool 0개가 함께 wiring된 뒤에만 열렸다.

작업:

- Desktop-owned v1 step parser/interpreter
- static cycle/limit validation
- canonical preview와 authorization ticket
- workflow run/step ledger
- crash, cancel, retry, resume 정책

완료 조건:

- 3개 이상 Tool step의 sample workflow 실행
- 승인 후 payload 변경 회귀 테스트
- Sidecar kill 시 effect 중복 실행 없음
- 실패 step과 다음 행동이 Activity/Task UI에 표시

구현 결과:

- frozen exact version을 invocation 직전 재조회·재hash·재검증하고, 후속 activation이 현재 run을 바꾸지 않음
- 실행 중 exact duplicate는 같은 Desktop task에 합류하고 terminal replay와 `needs_information` pointer는 schema 14에서 durable하게 복원
- ticket expiry는 trusted `TimeProvider`로 판정하고 effect reservation/ticket item CAS 전 effect 0회 보장
- Sidecar disconnect는 parent CTS를 즉시 취소하며 effect 전은 재시작 가능 취소, effect 결과 미확정은 `attention_required/review_outcome`으로 고정
- Sidecar final output은 100 KiB/depth 16/node 4,096이 exact 상한이고 outer envelope에만 32 KiB/depth 1/node 64 여유를 두며 C# optional JSON은 `null` 대신 absent로 직렬화
- `./scripts/verify.ps1`: Sidecar 123, Contracts 16, Application 158, Sidecar Integration 1, Desktop 529, warning/error 0
- `./scripts/smoke-ui.ps1`: IntegratedPages 6, TopLevelWindows 1, CompactLayout 760×500, focus/validation/sorting/conversation boundary/keyboard/Precision Workspace 통과, ApplicationErrors 0

### D-4 — Connection Profile과 typed Executor

typed executor 확장 순서:

1. internal HTTP — D-4 기반 수직 slice
2. SQL read-only — 별도 contract/risk 결정 후속 slice
3. mail draft/send — D-6 기준 수직 구현
4. Git read-only — 별도 contract/risk 결정 후속 slice

완료 조건:

- secret은 Credential Manager에만 저장
- Capability에는 Connection ID만 결합
- endpoint/account 변경 시 readiness와 grant 무효화
- timeout, result bound, fixed error, redaction test
- HTTP R3 canonical risk와 redirect 차단/매-hop target 재검증 test

D-4 기반 slice 구현 결과:

- Protocol 1.20, runtime snapshot v2와 SQLite schema 15로 logical slot/kind, exact-version local binding, revision-bound probe와 opaque CSPRNG binding generation을 연결
- local endpoint/profile/auth/token은 package/model/Sidecar IPC 밖에 두고 Bearer 원문은 Windows Credential Manager에만 저장; 8~1,280자 token68과 failed replace/delete 명시 복구 적용
- manifest/instructions/workflow/schema/test의 명백한 connection literal을 catalog/ZIP/persisted/runtime/orchestration ingress에서 중앙 차단하되 임의 난독화를 판별한다는 주장은 하지 않음
- actual probe, profile save/bind와 execution final resolve→response를 Desktop-wide shared lease로 직렬화해 target/credential TOCTOU 차단
- `connection.http.request.v1`을 Workflow-only `R3 / OneShot / Never / Effect`로 추가하고 HTTP/1.1 exact, connection close, 0-byte adapter content, redirect/cookie/proxy/default credential off, 30초·16KiB header·48KiB body·16,000 scalar 상한 적용
- 실제 premature EOF socket에서도 request/probe wire count 1회, declared/unknown-length N/N+1, invalid UTF-8, credential raw/JSON reflection output 0건, fixed disposition/ledger 회귀 연결
- Connections/Capability review UI, production SQLite stale CAS, auth-only invalidation과 actual Named Pipe local-detail 0건을 인수 증거로 연결
- SQL read-only, mail draft/send와 Git read-only는 generic HTTP/shell로 대체하지 않고 각 후속 typed slice에서 별도 계약·위험 결정을 추가
- `./scripts/verify.ps1`: Sidecar 123, Contracts 18, Application 187, Sidecar Integration 1, Desktop 596, warning/error 0
- `./scripts/smoke-ui.ps1`: IntegratedPages 7, TopLevelWindows 1, CompactLayout 760×500, focus/validation/sorting/conversation boundary/keyboard/Precision Workspace 통과, ApplicationErrors 0

### D-5 — Feature Builder

상태: 완료. Protocol 1.21·SQLite schema 16의 Desktop-owned proposal pipeline, 전체 verify와 Feature Builder UIA smoke를 완료했다. 상세 증거는 `D5_ACCEPTANCE.md`, 경계 결정은 ADR 0040을 따른다.

작업:

- builder session과 missing-information state
- Cline 기반 requirement collector
- manifest/workflow/test proposal 생성
- schema/static scanner
- dry-run과 diff review
- proposal apply/reject/quarantine/stale

완료 조건:

```text
“시스템 상태를 확인한 뒤 완료 알림을 보여 주는 기능을 추가해”
 → 알림 제목과 표현 방식 질문
 → system.get_status.v1 + system.show_notification.v1 기반 Workflow draft
 → static scan과 무효과 fixture dry-run
 → ReadyForReview 검토 초안
 → Capabilities에서 별도 검토·활성화
```

Agent가 활성 package를 직접 쓰지 않고 Desktop proposal pipeline만 사용해야 한다.

이전 예시였던 “지정 폴더의 파일을 유형별로 정리”는 현재 Workflow v1의 collection mapping과
`file.move.v1` prepared one-shot adapter가 없으므로 D-5 완료 사례로 주장하지 않는다. generic shell이나
model-direct 파일 효과로 우회하지 않으며, 전용 batch file mutation 계약·준비 효과·replay가 추가된 뒤
독립 수직 slice로 닫는다.

구현 결과:

- Sidecar는 기존 Cline provider/model을 재사용하는 Tool 0개·iteration 1회의 임시 Builder agent와 bounded `capability.proposal.generate`만 소유하고, Desktop이 질문 상태·correlation·secret 차단·canonical package admission을 다시 검증
- shared materializer/static scanner와 fixture 필수 no-effect schema dry-run을 통과한 동일 candidate에 대해서만 evidence CAS를 허용하고, apply는 active version을 바꾸지 않는 source `builder`의 `ReadyForReview` draft만 생성
- SQLite schema 16에 Builder session/proposal/test ledger와 crash recovery·terminal immutability를 추가하고, unsafe artifact는 원문 없이 metadata-only quarantine
- Capabilities 내부 Builder 화면에서 요구사항→질문→안전한 요약→검사→review-only apply/reject를 제공하며 일반/760×500 UIA 경계를 확인
- 표적 회귀: Application Feature Builder 40/40, Desktop Feature Builder 37/37, UI source 26/26
- `./scripts/verify.ps1`: Sidecar 135/135, Contracts 18/18, Application 243/243, Sidecar Integration 1/1, Desktop 645/645, warning/error 0; PowerShell parse, release manifest와 evidence 검증 통과
- `./scripts/smoke-ui.ps1`: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500, 열거된 UIA 검사 통과, ApplicationErrors 0
- `npm audit`의 기존 transitive low severity advisory 1건은 verify exit 0 상태로 계속 추적하며 호환성 근거 없이 의존성을 강제 변경하지 않음

### D-6 — 이메일 기준 수직 구현

D-6은 다음 세 수직 slice로 나눈다. D-6A와 D-6B는 인수 완료했고 D-6C0~2 text-only SMTP는 구현 완료 후 최종 인수 검증 중이다. attachment D-6C3/4는 메일 확장보다 범용 Capability 증명을 우선하기 위해 보류한다.

1. **D-6A — text-only no-send draft · 인수 완료**
   - recipient/subject/plain-text body를 bounded typed schema로 정규화하고 `draft`와 `send`를 분리한다.
   - 실제 Connection, secret, socket과 attachment를 사용하지 않는 Desktop-owned 정적 검사와 무효과 fixture만 닫는다.
2. **D-6B — fixed Mail Gateway와 durable outbox send · 인수 완료**
   - 임의 사내 HTTP API mapping이나 generic POST가 아니라 검토된 고정 Mail Gateway contract 하나를 지원한다.
   - `mail.send.v1`은 Desktop R3/AllowOnce/one-shot 경로를 통과하고 exact payload·binding hash와 idempotency key에 결합된 outbox로 실행한다.
   - 초기 adapter의 retry budget은 0이다. 승인 한 번에 POST는 최대 1회이고, ambiguous 결과는 같은 operation의 GET 최대 1회로만 즉시 조정하며 미확정 결과를 새 전송으로 자동 반복하지 않는다.
3. **D-6C0~2 — text-only SMTP reference · 구현 완료/최종 검증 대기**
   - schema 19 조회 preflight와 schema 20 typed SMTP profile/probe/binding/journal을 추가했다.
   - SMTP는 TLS/auth/credential, stable Message-ID와 DATA 이후 outcome-unknown 경계를 별도 adapter/replay로 제공한다. 서버 차원의 idempotency를 주장하지 않으며 결과 미확정 뒤 자동 재전송하지 않는다.
   - 기존 Protocol 1.23 `mail.send.v1`을 그대로 사용하고 Capability의 exact connection kind가 Mail Gateway 또는 SMTP adapter를 결정한다. 모델 입력에는 transport/host/port/auth/sender가 없다.
4. **D-6C3/4 — attachment · 보류**
   - path/identity/hash/size, reparse와 승인 후 변경을 재검증하는 별도 계약/메모리 snapshot 계획은 유지한다.
   - 메일 전용 기능을 계속 확장하기 전에 같은 Catalog→Builder→Connection→Workflow→승인→typed Executor 경로로 비메일 Capability를 하나 더 완성한다.

D-6A 구현 결과:

- Protocol 1.22에 bounded plain-text `mail.create_draft.v1` input/result 계약을 추가하고 C#/TypeScript contract hash `a107ca46484eebc707c568514187c9896275adb51d152cb0490a847eafc415b7`로 생성 parity를 고정
- transport-independent `MailDraftPolicy`가 recipient/Unicode/UTF-8/control/LF/hash 정책을 소유하고 Desktop Workflow-only R0 Observation adapter는 hash/count/length metadata만 반환
- ordinary Tool/Sidecar direct registration, network, Connection, Credential Manager, SQLite draft 저장과 sender/endpoint/header/HTML/attachment를 모두 열지 않음
- 실제 수직 test가 parser→static validator→orchestrator→SQLite run/step ledger와 Feature Builder no-effect→`ReadyForReview` 경로를 통과하고 recipient/subject/body canary 비노출을 확인
- 수직 test에서 드러난 문자열 배열 `uniqueItems` 의미를 Application runtime/static validator, package admission과 Sidecar projection에 동일하게 반영. 일반 regex `pattern`은 Workflow subset에 열지 않고 결과 hash를 71자로 제한하며 producer가 exact 형식을 강제
- 표적 회귀: Contracts generated 13/13, Application mail/schema 101/101, Desktop mail 7/7
- `./scripts/verify.ps1`: Sidecar 137/137, Contracts 20/20, Application 296/296, Sidecar Integration 1/1, Desktop 650/650, warning/error 0; PowerShell parse, release manifest/evidence와 contract generation parity 통과
- 상세 증거는 `D6A_ACCEPTANCE.md`와 ADR 0041을 따른다.

D-6B는 구현 의미를 다음처럼 더 작게 고정한다.

1. **B1 — 계약·정책·durable submission journal · 구현 완료**
   - Protocol 1.23 `mail.send.v1`, D-6A hash 재검증, 지배하는 draft step binding과 `mail-api` connection kind를 추가한다.
   - schema 17에는 원문 없는 operation/idempotency/outcome metadata만 저장하고 CAS·restart barrier를 닫는다.
2. **B2 — fixed Gateway·R3 prepared effect·UX · 인수 완료**
   - 별도 typed Mail Gateway profile/probe/binding, Credential Manager token과 고정 submit/reconciliation wire를 추가한다.
   - full-mail AllowOnce preview와 queued/delivered 구분, 결정적 실제 fake Gateway 수직·replay·raw canary, 저장소 전체 verify와 UI smoke로 인수를 닫았다.

D-6B 현재 구현 결과:

- SQLite schema 18에 HTTP와 분리된 typed Mail Gateway profile/probe/binding 저장소와 관리 서비스를 추가하고, Mail Gateway 전용 Credential Manager target, target/credential revision stale 처리와 composite readiness를 연결
- 고정 Gateway v1 probe/submit/reconciliation wire와 strict response bound, Mail/HTTP 실행 resolver를 구성. production composition은 HTTP와 Mail profile·credential·probe·binding mutation 및 두 kind의 execution final resolution 모두에 동일한 `ConnectionExecutionLeaseCoordinator`를 주입해 mixed aggregate TOCTOU를 차단
- production `mail.send.v1`을 Workflow-only `R3 / OneShot / Never / Effect` prepared Tool로 연결. journal stage/CAS 뒤 POST를 최대 한 번만 수행하고 ambiguous 결과는 같은 operation을 즉시 GET 최대 한 번 조회하며 두 번째 POST나 새 key 자동 retry를 하지 않음
- 새 AllowOnce 승인은 같은 draft/binding의 network-started unresolved `submitting | outcome_unknown`을 repository `LIMIT 2`로 읽어 반환 row를 최대 두 건으로 제한하고 첫 operation 하나만 GET 최대 한 번 조정. 두 번째 row가 보이면 첫 row가 terminal이어도 POST 0회로 다음 승인을 요구
- immutable `created_at_utc`는 stage insert가 staged→submitting CAS와 POST보다 앞서 durable commit된 첫 network attempt의 보수적 lower-bound이고, `updated_at_utc`는 240초 age 기준이 아닌 transition/startup recovery의 더 늦은 durable time lower-bound다. `ExecuteOnceAsync` 시작에서 주입된 `TimeProvider`로 `executionStartedAtUtc`를 읽고 immediate/historical GET 응답 직후 같은 clock으로 `lookupObservedAtUtc`를 재샘플해 shared lease 대기와 GET 경과를 포함한다. 네 시각이 모두 UTC이고 `lookupObservedAtUtc >= executionStartedAtUtc >= max(created_at_utc, updated_at_utc)`, `lookupObservedAtUtc - created_at_utc <= 240초`일 때만 exact `not_found`를 terminal `failed_known`으로 확정한다. 어느 clock read든 실패하거나 non-UTC·durable lower-bound 위반·관측 역행·240초 초과이면 unresolved와 추가·새 POST 0회로 fail closed. 회귀는 실행 `t+230초`/관측 `t+241초`인 window 초과, 실행 `t+310초`/rollback 관측 `t+230초`인 관측 역행, 생성 `t`/recovery 갱신 `t+310초`/실행 `t+100초`/관측 `t+110초`인 durable lower-bound 위반을 고정
- Connections에서 typed Mail Gateway root HTTPS endpoint, 고정 sender와 masked Bearer token save/probe를 제공하고 Capability review에서 `mail-api` binding을 분기 처리. 740×500 승인 창은 sender/connection, 전체 recipients, subject와 plain-text body를 하나의 keyboard/screen-reader 접근 가능한 scroll 영역에 표시
- 표적 증거: Contracts D-6B selected 15/15, Application D-6B selected 92/92, Desktop `MailSendTool` + vertical + outbox selected 30/30, broader Desktop security/wiring selected 121/121, Desktop targeted build warning/error 0
- `./scripts/verify.ps1` exit 0: PowerShell 5.1 parse, release manifest/evidence, contract generation parity, TypeScript check/build 통과. Sidecar 138/138, Contracts 22/22, Application 399/399, Sidecar Integration 1/1, Desktop 750/750, .NET build warning/error 0
- release manifest fixture의 current database schema 기대값을 16→18로 수정해 schema 17/18 migration과 release evidence를 일치시킴
- `./scripts/smoke-ui.ps1` 통과: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500, FieldValidation, ManagementSorting, ConversationBoundary, KeyboardNavigation, PrecisionWorkspace 통과, ApplicationErrors 0. `InputFocusRestored=not-observed-window-inactive`는 비활성 창이라 관측되지 않은 상태이며 실패가 아님
- `npm audit`의 기존 transitive low severity advisory 1건은 남아 있지만 verify exit 0이며 호환성 근거 없이 lockfile을 강제 변경하지 않음
- D-6C preflight 완료: SQLite schema 19에서 `submitting | outcome_unknown` exact predicate와 `(draft_hash, binding_hash, updated_at_utc DESC, operation_id)` 순서를 가진 partial composite index로 교체하고 production 조회가 `INDEXED BY`로 이를 요구하게 했다. 실제 production SQL의 결정적 migration/query-plan 회귀는 해당 index 사용, table scan 0건과 temporary B-tree 0건을 고정하며 release manifest fixture도 current schema 19로 갱신했다. 표적 `MailOutboxRepositoryTests` 11/11 통과

여기서 durable outbox는 offline mail payload queue가 아니라 durable submission journal이다. 실행 invocation의 수신자·제목·본문을 outbox/Workflow ledger/activity/audit/diagnostic에 추가 복제하지 않으며 network 시작 전 crash 난 operation은 취소하고 새 Workflow·새 승인을 요구한다. 사용자가 채팅에 직접 입력한 문장은 기존 conversation/FTS 정책으로 영속되고 모델 context로 전달되며, schema-bound Workflow input은 Sidecar→Desktop IPC를 통과한다. 사용자가 검토·승인한 Capability 정의의 literal도 Catalog 정책으로 영속될 수 있다. Sidecar는 원문을 durable storage나 diagnostic log에 남기지 않는다. 재시작 뒤 발송 가능한 DPAPI payload queue가 실제로 필요해지면 retention, backup, PC/user binding과 재승인을 별도 ADR로 결정한다. 상세 경계는 ADR 0042, ADR 0043과 `D6B_ACCEPTANCE.md`, `D6C_ACCEPTANCE.md`를 따른다.

Feature Builder는 secret을 수집하지 않는다. D-6B Builder 질문은 `mail-api` 기능 요구와 비밀 없는 schema 선택으로 제한한다. endpoint와 고정 sender는 Desktop Connections 화면에서, Bearer token은 같은 화면의 masked 입력으로만 받고 원문은 Credential Manager에 저장한다. package, Builder session, model prompt, Sidecar IPC와 SQLite journal에는 credential 원문을 넣지 않는다. `smtp`/password 질문은 D-6C에서만 추가한다.

완료 조건:

```text
기능 요청
 → Builder가 mail-api와 비밀 없는 기능 요구만 질문
 → Desktop Connections에서 endpoint/sender와 masked Bearer token 별도 입력
 → Credential Manager 저장과 Connection save
 → 무효과 draft test
 → Connection probe와 exact-version rebind/readiness 확인
 → Capability 생성
 → 활성화
 → 메일 작성
 → 수신자·제목·plain-text 본문과 전송 대상을 AllowOnce 승인
 → durable submission journal과 Gateway `queued` 접수 확인
 → 신뢰 가능한 별도 final evidence가 있을 때만 `delivered` 확인
 → token 누락·credential rotation·endpoint 장애는 Capability version을 바꾸지 않고
    Connection save → probe → rebind/readiness로 복구
 → Workflow 또는 메일 형식 정의 결함일 때만 active version 기반 새 proposal
```

Outlook UI와 webmail 자동화는 동일 단계에 섞지 않고 기존 UIA/Browser 기반 후속 Capability로 분리한다.

### D-7 — Operations UX

작업:

- Capabilities, Builder, Connections page
- Tasks와 Automations 통합 탐색
- attention chip
- parent/child run rail
- approval/proposal/history 연결
- 기능별 실행 수, 성공률, 반복 오류, 평균 시간

완료 조건:

- 사용자가 활성 기능, 준비되지 않은 기능, 실행 중 작업, 실패 원인, rollback을 한 셸에서 찾을 수 있음
- 760×500, keyboard, high contrast, virtualization 기준 유지
- WPF/Sidecar smoke 확장

### D-8 — 사내 Capability pack

이메일 기준 구현이 안정된 뒤 다음 순서로 추가한다.

1. Git 변경사항과 주간보고서
2. Confluence/사내 문서 읽기
3. SQL 통계와 Excel 보고서
4. UIA 기반 사내 프로그램
5. Docker/Kubernetes 상태 읽기
6. 승인 기반 변경·배포 작업

각 통합은 독립 `.ligcap`, Connection kind, Tool schema, replay, adapter test를 가진다.

### D-9 — 개선 제안과 사내 공유

작업:

- 실패·사용자 교정 기반 improvement proposal
- 과거 session의 bounded 수동 review
- 사내 Git source metadata와 update 확인
- package hash, source, version diff
- offline bundle import

완료 조건:

- 자동 적용 없음
- active version을 수정하지 않음
- source update도 신규 draft와 review를 통과
- public internet marketplace 의존 없음

### D-10 — 고급 Script·Extension·Remote Runner

실제 요구와 운영 기준이 확보된 뒤에만 착수한다.

- registered PowerShell/CLI profile
- 별도 worker 기반 C# extension
- signed/hash-pinned offline dependency bundle
- Remote Runner mutual authentication과 machine allowlist
- 중앙 LIGClaw보다 좁은 Tool/Connection scope

이 단계는 Cline Sidecar를 대체하지 않는다. Remote Runner는 다른 PC에서 Desktop 승인으로 확정된 Tool을 실행하는 선택형 실행 노드다.

## 15. 첫 번째 MVP 완료 기준

다음 시나리오가 모두 동작하면 Capability Platform MVP를 완료한다.

### 기능 추가

```text
사용자: “사내 메일 기능을 추가해.”
LIGClaw: 연결 방식과 필요한 항목을 1~3개씩 질문
LIGClaw: draft, workflow, test, 최대 위험도와 전송 대상을 표시
사용자: test 결과와 diff를 확인하고 활성화
```

### 기능 실행

```text
사용자: “김대리에게 보고서 초안을 보내줘.”
Sidecar: bounded 후보 중 exact Capability version 선택
Desktop: exact Mail Gateway Connection과 draft hash를 재검증
Desktop: 수신자·제목·plain-text 본문과 전송 대상을 preview
사용자: 이번 실행 승인
Desktop: fixed Gateway 제출, durable journal과 `queued`/`delivered` 구분 기록
```

### 정보 누락

```text
메일 token 없음
 → secret이 필요하다는 구조화 상태
 → Connection 수정 화면
 → 연결 시험
 → 기존 Capability는 수정하지 않고 readiness만 갱신
```

### 기능 수정

```text
사용자: “다음부터 제목에 [업무]를 붙여.”
 → active version 기반 proposal
 → instructions/workflow diff
 → fixture test
 → 사용자 승인
 → 새 version 활성화
```

### rollback

```text
새 version 반복 실패
 → attention 표시
 → 이전 version과 차이 확인
 → rollback 승인
 → 다음 run부터 이전 snapshot 사용
```

### Sidecar 장애

```text
workflow 도중 Sidecar 종료
 → Desktop parent CTS 즉시 취소
 → effect 전은 cancelled/retry_from_start
 → 시작됐거나 결과 미확정인 effect는 attention_required/review_outcome
 → Sidecar 재시작
 → same-run resume 없이 사용자가 결과를 검토하고 필요 시 새 run 승인
```

## 16. 테스트 전략

### 계약과 replay

- Capability snapshot C#/TypeScript 생성 일치
- fixed catalog revision/hash fixture
- native Tool Calling과 text fallback
- Feature Builder proposal schema
- Workflow step event sequence
- stale version과 duplicate call 거부

### Domain/Application

- semantic version과 state transition
- activation/rollback transaction
- readiness aggregation
- workflow cycle/limit
- grant/ticket payload binding
- unattended eligibility
- proposal stale/quarantine

### Sidecar

- dynamic AgentTool projection
- `prepareTurn`이 transcript를 오염시키지 않음
- snapshot 변경 session retire/recreate
- Cline abort/restore/event mapping
- Workflow cancel, timeout, tool failure
- catalog 및 MCP 변경 경합
- Workflow projection이 Sidecar에서 effect를 직접 실행하지 않는 경계

### Desktop integration

- package import traversal/symlink/size
- SQLite migration/backup/restore
- Credential Manager rollback
- HTTP/SQL/mail fake server
- approval 후 payload tamper
- Sidecar kill과 process tree cleanup
- duplicate external effect 방지

### 폐쇄망 acceptance

- 공용 DNS와 인터넷 없이 실행
- package 설치 중 dependency download 0건
- 사내 vLLM/MCP/HTTP endpoint만으로 기준 시나리오 통과
- 진단 번들에 prompt, token, 파일 원문, Connection secret 0건
- unsigned/untrusted package 정책 검증

## 17. 의도적으로 뒤로 미루는 기능

- Sidecar 제거와 Cline SDK의 .NET 이전
- WPF 전면 교체와 Blazor Hybrid 전환
- public Capability marketplace
- Agent의 package 자동 설치·자동 활성화
- 임의 PowerShell/cmd/shell 문자열 실행
- 메인 Desktop process의 untrusted DLL hot-load
- arbitrary HTML/JavaScript dashboard widget
- 외부 메시징 channel과 공개 webhook
- 관리자 권한 자동 상승
- 무제한 SQL write, 파일 삭제, 서비스 변경
- Remote Runner의 인터넷 노출

이 항목은 영구 제외가 아니다. 기존 typed Tool과 Capability/Workflow로 해결할 수 없는 실제 요구, 복구 경계, 배포 운영 모델이 확인된 뒤 독립 ADR과 단계로 추가한다.

## 18. 전체 실행 순서

```text
C단계 외부 release gate ───────────────────────────────┐
                                                       │
D-0 결정/기준선                                       │
  → D-1 Catalog                                       │
  → D-2 Cline Projection                              │
  → D-3 Workflow                                      │
  → D-4 Connections                                   │
  → D-5 Feature Builder                               │
  → D-6 Email reference                               │
  → D-7 Operations UX                                 │
  → D-8 Internal capability packs                     │
  → D-9 Improvement/share                             │
  → D-10 Script/Extension/Remote Runner (conditional) ┘
```

인수 완료한 최신 수직 slice는 Sidecar 제거가 아니라 D-6B Desktop-owned fixed Mail Gateway다. D-6A no-send draft의 hash를 지배적 binding으로 사용하고 metadata-only journal과 R3 one-shot send 경계를 닫았다.

```text
bounded recipient/subject/plain-text body
 → Desktop MailDraftPolicy와 canonical draft hash
 → Workflow-only R0 Observation
 → 콘텐츠 없는 hash/count/length 결과
 → no-send/no-persist ledger evidence
```

D-6A는 저장소 전체 verify와 실제 parser→validator→orchestrator→ledger 수직 회귀를 통과해 no-send draft slice를 닫았다. D-6B도 typed persistence/readiness, fixed wire/probe/resolvers, production R3 prepared send Tool, UI, 결정적 실제 수직·replay·raw canary와 전체 verify/UI smoke를 통과해 인수 완료했다. generic HTTP POST로 우회하지 않으며 attachment/SMTP는 D-6C, SQL read-only/Git read-only는 각각 별도 typed slice로 남긴다.
