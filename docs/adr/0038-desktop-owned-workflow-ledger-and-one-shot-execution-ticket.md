# ADR 0038: Desktop-owned Workflow ledger와 one-shot execution ticket

- 상태: 승인 · D-3 구현됨
- 날짜: 2026-08-02

## 배경

D-2는 prompt-only Capability를 frozen run snapshot으로 Cline에 투영하고, 선택 뒤 일반 Tool도 Desktop 정책·승인·감사 경로를 통과하게 했다. D-3에서는 여러 Tool을 선언형으로 조합하는 Workflow를 추가해야 한다.

Workflow를 Sidecar에서 해석하거나 선택된 `requiredTools`를 그대로 모델에 노출하면 Desktop이 검토한 step 순서와 실제 effect가 달라질 수 있다. 기존 Tool adapter의 preview 상태는 모두 batch authorization ticket으로 재사용할 수 있는 immutable handle도 아니다. 또한 기존 Agent job의 자동 재대기 복구를 effect Workflow에 적용하면 crash 지점에 따라 외부 효과를 중복 실행할 수 있다.

SQLite connection이나 transaction을 승인 대기와 Tool 실행 동안 유지하는 방식도 채택할 수 없다. Desktop repository는 같은 gate로 audit, undo와 상태를 기록하므로 장시간 transaction은 재진입 deadlock과 불명확한 실행 결과를 만든다.

## 결정

### 소유권과 호출 경계

- Workflow 정의 검증, validated plan 생성, 실행, durable run/step ledger와 복구는 Desktop이 소유한다.
- Sidecar는 Cline 후보 선택과 model argument 수집만 소유한다. Workflow JSON, step payload나 execution ticket을 저장·해석하지 않는다.
- Workflow candidate의 model Tool이 선택되면 Sidecar는 서로 다른 bounded Tool call ID로 `capability.select.v1`을 먼저 완료하고 그 뒤 `workflow.invoke.v1` control-plane 호출을 보낸다.
- `workflow.invoke.v1`은 `WindowsToolHost` adapter로 등록하지 않는다. Desktop 전용 Workflow orchestrator가 exact `CapabilityInvocationContext`를 검증하고 immutable catalog version을 다시 읽어 content hash와 definition을 재검증한다.
- Workflow 선택 뒤 Sidecar가 모델에 노출하는 일반 Desktop Tool은 0개다. 내부 `requiredTools`는 Desktop interpreter만 사용하며 Sidecar의 직접 일반 Tool 호출은 고정 오류 `capability.workflow.direct_tool_denied`로 거부한다.
- 처리된 Workflow의 업무 실패는 bridge 자체 실패와 구분한다. context/schema/policy 거부만 `workflow.result` outer envelope의 `success=false`이고, 시작된 Workflow는 구조화된 `status`와 `nextAction`을 `success=true` output으로 반환한다.

### Workflow v1 정의

단일 원천은 `contracts/capabilities/workflow-v1.schema.json`으로 둔다. 첫 버전은 다음 step만 허용한다.

- `input.map`
- `tool.call`
- `condition`
- `transform`
- `needs_information`
- `output`

정적 제한은 다음과 같다.

- step 최대 20개, branch depth 최대 5
- step ID와 property의 ordinal duplicate/case collision 거부
- edge는 뒤쪽 ordinal만 참조하고 cycle, dangling, unreachable node를 거부
- Tool 이름은 canonical literal만 허용
- 모든 `tool.call`의 정확한 Tool 집합이 manifest `requiredTools`와 일치
- manifest `maximumRisk`는 선언 상한이다. Desktop이 계산한 actual maximum risk가 이를 넘으면 거부하고 승인·ticket에는 actual risk를 사용
- input 32 KiB, step output 64 KiB, final output 100 KiB의 UTF-8 byte 상한
- retry 가능성은 risk와 분리한 Desktop metadata다. `maxRetries`는 0~3이며 첫 slice에서는 `read_only` observation만 같은 process 안에서 재시도
- transform은 JSON Pointer와 작은 고정 연산 allowlist만 사용
- loop, parallel, recursion, arbitrary expression/regex/script/shell, 동적 Tool 이름과 object spread 금지
- `workflow.invoke`, `capability.select`, `subagent.run`, Agent job control과 임의 MCP argument Tool의 재귀·메타 실행 금지
- `needs_information`은 v1 terminal이며 effect 뒤에 도달 가능한 경로를 정적 거부한다. 새 입력으로 이어지는 resume는 새 run으로만 수행한다.

Definition은 activation 시와 invocation 직전에 같은 parser/static validator로 검사한다. 지원하지 않는 Tool input/result schema는 activation을 fail-closed 한다.

### Immutable validated plan과 per-step execution ticket

Workflow parser/static validator의 결과는 `ValidatedWorkflowPlan`으로 고정한다.

- 전체 graph와 successor ordinal
- definition/input-contract/plan hash
- canonical Tool과 Desktop canonical risk
- preparation mode, retry/effect class
- generated Tool input/result contract hash

실제 payload와 target binding은 선택 branch와 이전 step 결과가 확정된 실행 직전에 resolve한다. D-3 first slice는 plan-wide batch 승인을 구현하지 않고 effect step마다 single-item ticket과 `AllowOnce` 승인을 만든다.

effect step을 실행하려면 adapter가 명시적으로 one-shot preparation을 지원해야 한다. 기본 adapter는 지원하지 않는다.

```text
IWorkflowOneShotToolAdapter
  → PrepareWorkflowEffect
  → immutable PreparedWorkflowEffect
       ToolName / Risk / ExpiresAtUtc
       ApprovalPrompt
       CanonicalBindingHash
       CanonicalPayloadHash
       single-use ExecuteOnceAsync
       DisposeAsync
```

- D-3 첫 slice에서는 `system.show_notification.v1`만 one-shot effect adapter로 만든다.
- 파일·창처럼 preview 때 해석한 대상 identity를 mutable adapter state에 두는 Tool은 per-invocation immutable prepared handle로 바꾸기 전까지 Workflow effect allowlist에서 제외한다.
- ticket은 Desktop 메모리와 ledger에만 존재하고 Sidecar나 모델에 전달하지 않는다.
- capability exact identity, workflow/input/plan hash, step sequence, canonical Tool/risk, payload/binding hash와 expiry를 결합한다.
- ticket은 `AllowOnce`만 지원하며 session/30일 persistent grant를 만들지 않는다.
- payload, order, risk나 target binding이 바뀌면 effect 전에 고정 오류로 거부한다.
- prepared effect는 검증·복사된 immutable payload와 target identity를 직접 소유한다. 원본 dictionary나 adapter 공유 cache를 다시 읽지 않고 `Interlocked` single-use, expiry와 `IAsyncDisposable` discard를 제공한다.
- 실행 결과 disposition은 `not_started`, `succeeded`, `failed_known`, `unknown`으로 구분한다. effect reservation 뒤 adapter가 `not_started`를 증명하지 못한 예외·취소는 `outcome_unknown`이다.

### Durable at-most-once ledger

SQLite schema 14에 기존 immutable `capability_versions.workflow_json`을 정의 원본으로 유지하고 다음 table을 추가한다.

- `workflow_runs`: parent conversation/run/tool-call/idempotency, exact capability context, workflow/input/plan hash, status/current sequence/next action/error, redacted summary/result hash, bounded `required_pointers_json`과 revision
- `workflow_authorization_tickets`: per-step 승인 header와 exact hashes, actual maximum risk, status, expiry, 승인·철회 시각
- `workflow_authorization_ticket_items`: conversation/parent run/workflow run, step sequence와 random nonce hash, Tool/risk, payload/binding hash, expiry와 one-shot 소비 상태
- `workflow_step_runs`: sequence/attempt, deterministic Tool call ID, canonical Tool/risk/retry class, payload/binding hash, ticket consumption, status/output hash/redacted summary/fixed error

`(parent_run_id, idempotency_key)`, `(parent_run_id, parent_tool_call_id)`와 step Tool call ID는 unique다. 원문 prompt, Workflow input, Tool input/output, 파일 내용과 secret은 ledger에 저장하지 않는다.

Effect 경계는 반드시 다음 순서를 따른다.

```text
resolve/immutable prepare
 → AllowOnce 승인
 → 짧은 BeforeEffect transaction
    (step planned→executing + 해당 ticket item ready→consumed CAS)
 → transaction 종료
 → prepared effect ExecuteOnce
 → 짧은 completion transaction
```

- durable step reservation과 해당 ticket item compare-and-swap가 함께 성공하기 전에는 effect를 시작하지 않는다.
- ticket expiry는 repository에 주입된 `TimeProvider`의 현재 시간으로 판정한다. 호출자가 제공한 과거 `StartedAtUtc`로 만료를 되돌릴 수 없다.
- Workflow ledger와 승인/ticket 저장은 mandatory다. 이 저장 실패를 무시하고 effect를 진행하지 않는다. 기존 일반 operational audit의 best-effort 정책은 D-3에서 전역 변경하지 않는다.
- DB transaction이나 repository gate를 승인 UI와 Tool 실행 동안 잡지 않는다.
- completion 기록 뒤 Sidecar로 결과 전달이 실패해도 완료된 effect 상태를 실패로 되돌리지 않는다.
- DB와 외부 effect 사이의 원자 transaction은 불가능하다. 자동 실행 경로에서 non-idempotent effect attempt를 최대 1회만 시작하고, 결과가 불명확하면 재실행하지 않는 의미로 at-most-once를 사용한다.

Idempotency key는 모델 인자가 아니라 Sidecar가 원래 Cline Tool call identity에서 생성한다. 실행 중 같은 exact identity/input/hash의 duplicate는 Desktop in-memory task 하나를 함께 기다리고, terminal 후 duplicate는 기존 durable 결과를 재생한다. `needs_information`의 bounded RFC 6901 pointer도 run row에 저장해 같이 재생한다. 같은 key의 다른 identity/input은 `workflow.idempotency_conflict`로 거부한다. deterministic step Tool call ID는 workflow run ID, sequence와 attempt에서 만든다.

### 취소, crash와 재개

- Workflow child run은 Sidecar conversation과 구분되는 Desktop CTS, Tool invocation policy, frozen authorization snapshot과 ledger를 가진다.
- reservation 전 취소는 `cancelled`다. effect 시작 뒤 취소·timeout·Desktop crash로 결과를 확정할 수 없으면 step과 run을 `outcome_unknown`/`attention_required`로 둔다.
- 자동 retry는 immutable input이 메모리에 남은 같은 process 안에서만 허용한다. Desktop restart 뒤에는 R0도 자동 재실행하지 않으며 observation은 `interrupted`, effect는 `outcome_unknown`으로 복구한다.
- startup에서 active ticket은 모두 revoke한다. Agent job처럼 interrupted Workflow를 자동 pending으로 되돌리지 않는다.
- v1은 crash 후 같은 run의 step resume를 제공하지 않는다. 같은 process의 read-only observation bounded retry와 실행 중 exact duplicate task 합류만 허용하며, live task가 없는 in-progress row는 자동 재시작하지 않는다.
- Desktop restart 뒤 원문 state가 없으면 v1 같은-run resume를 지원하지 않는다. 처음부터 다시 실행하려면 중복 effect 가능성을 경고하는 새 run과 새 승인이 필요하다.
- Sidecar disconnect 상태를 받으면 Desktop UI가 active parent run을 즉시 종료하고 run CTS를 취소한다. effect 전이면 unused ticket을 revoke하고 `cancelled/retry_from_start`로 정리할 수 있다. effect 실행이 시작됐거나 adapter disposition을 확정할 수 없으면 `attention_required/review_outcome`으로 승격하고 새 effect step을 시작하지 않는다. bridge disconnect는 pending promise를 즉시 실패시키고 late result를 거부하며, 결과 전달 실패는 ledger 상태를 바꾸지 않는다.
- Desktop Workflow deadline은 승인 대기를 포함해 8분 45초, Desktop bridge timeout은 10분, Workflow AgentTool timeout은 11분으로 두어 `Desktop deadline < bridge timeout < AgentTool timeout`을 유지한다.

### 첫 수직 slice

첫 acceptance Workflow는 다음 canonical Tool 3개를 순차 실행한다.

1. `system.get_status.v1` — R0
2. `file.get_metadata.v1` — R0
3. `system.show_notification.v1` — R1, immutable prepared effect와 one-shot ticket

Protocol 1.19에 `workflow-v1` definition, Workflow candidate discriminator, `workflow.invoke.v1` input/result와 `workflow.runtime.v1` handshake capability를 함께 추가했다. Desktop parser/static validator, schema 14 ledger, one-shot ticket과 orchestrator가 wiring된 경우에만 Workflow gate를 열고 Sidecar가 runtime capability를 광고한다.

### 구현된 계약 세부 경계

- Protocol 1.19 생성 hash는 `b92843376eb07e0a2af0b1472af9532124c399283112a9d5fd0f84b016763571`이다.
- Sidecar Workflow final `result`는 UTF-8 100 KiB, depth 16, node 4,096이 exact 상한이다. 상태·summary·pointer를 담는 outer envelope에만 32 KiB/depth 1/node 64 여유를 두며 final output 상한은 늘리지 않는다.
- 생성된 C# Workflow result record의 optional property는 `JsonIgnoreCondition.WhenWritingNull`로 absent를 wire `null`과 구분한다. outer success/failure envelope도 `output` 또는 `error`만 직렬화한다.
- Activity는 Workflow terminal/running 상태, 마지막 유효 실패 step과 fixed next action을 redacted metadata로 표시한다. `attention_required`와 `interrupted`를 일반 `failed`로 합치지 않는다.

## 검증 기준

- parser/static validator: duplicate/case collision, cycle, dangling/unreachable, 20-step/depth, pointer dominance, risk/Tool set와 meta/shell 거부
- `workflow-v1` contract는 test-only Ajv 2020-12 validator와 Desktop parser/static validator가 같은 shared parity fixture를 통과해야 한다. Ajv는 Sidecar runtime dependency가 아니다.
- package/catalog: prompt 회귀, Workflow import/export, size/path/secret과 activation 직전 재검증
- compiler/interpreter: 3-Tool fixture, condition/transform/needs-information, byte/timeout bound
- ticket: payload/order/risk/target tamper, expiry와 duplicate consumption 시 effect 0회
- schema 13→14 migration, legal CAS transition, startup recovery와 원문 비저장
- reservation 저장 실패 시 effect 0회, ticket이 persistent grant를 만들지 않음
- Sidecar selector→invoke distinct ID, exact context와 ordinary Tool 0개
- stale/noncandidate/missing context/duplicate idempotency replay
- reservation 전, reservation 후/effect 전, effect 후/completion 전, completion 후/result 전 kill barrier에서 자동 non-idempotent effect attempt 최대 1회와 `outcome_unknown` 비재실행
- Activity UI의 failed step/next action과 상태 label, 전체 verify와 UI smoke

2026-08-02 D-3 구현 후 `./scripts/verify.ps1`은 Sidecar 123, Contracts 16, Application 158, Sidecar Integration 1, Desktop 529 test와 .NET build warning/error 0으로 통과했다. `npm audit`의 기존 `dify-ai-provider` 하위 `@ai-sdk/provider-utils` low advisory 1건은 verify를 실패시키지 않았고 lockfile 강제 변경을 적용하지 않았다. `./scripts/smoke-ui.ps1`도 IntegratedPages 6, TopLevelWindows 1, CompactLayout 760×500, focus/validation/sorting/conversation boundary/keyboard/Precision Workspace와 ApplicationErrors 0으로 통과했다.

## 결과

- 모델이 Workflow step이나 effect Tool을 임의로 추가·재정렬할 수 없다.
- 승인한 plan과 실제 실행 payload/target이 exact hash로 결합된다.
- crash 시 중복 effect보다 사용자 확인이 필요한 `outcome_unknown`을 선택한다.
- Desktop의 기존 Tool policy를 재사용하면서도 Workflow용 durable reservation을 effect 전에 추가한다.

## 대안

- Sidecar에서 Workflow를 해석하는 방식은 정책·SQLite·복구 소유권을 깨므로 채택하지 않는다.
- 선택 뒤 모델에 모든 `requiredTools`를 노출하는 방식은 승인된 plan 외 재호출을 허용하므로 채택하지 않는다.
- 기존 adapter preview dictionary를 batch ticket으로 재사용하는 방식은 invocation별 target binding과 single-use를 보장하지 못하므로 채택하지 않는다.
- Agent job의 interrupted→pending 자동 복구를 재사용하는 방식은 non-idempotent effect를 반복할 수 있어 채택하지 않는다.
- 승인/Tool 실행 동안 SQLite transaction을 유지하는 방식은 deadlock과 긴 lock을 만들므로 채택하지 않는다.
