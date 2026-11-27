# D-3 Workflow Runtime과 execution ticket acceptance

> 상태: 구현·전체 verify·UI smoke 완료
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0038-desktop-owned-workflow-ledger-and-one-shot-execution-ticket.md`
> 계약/저장소: Protocol 1.19 · SQLite schema 14

## 결과

D-3는 Cline Node.js Sidecar를 유지하면서 선언형 Workflow의 정의 검증, 해석, Tool 실행, 승인, SQLite 원장과 복구를 Desktop 소유 경계로 추가했다. Sidecar는 frozen Capability 후보를 선택하고 bounded input으로 `workflow.invoke.v1`을 요청할 뿐 Workflow JSON, step, ticket과 nonce를 받거나 해석하지 않는다.

```text
Cline Workflow candidate 선택
 → capability.select.v1 exact handshake
 → workflow.invoke (distinct call ID / deterministic idempotency key)
 → Desktop parent-run · frozen Capability authorization
 → exact immutable version 재조회 · hash · parser/static validator 재검사
 → schema 14 workflow run 생성 또는 exact duplicate 합류
 → Desktop-owned WorkflowInterpreter
 → observation 또는 prepared one-shot effect
 → step/run terminal 상태와 redacted metadata 원장 기록
 → workflow.result
 → 다음 Cline turn에는 일반 Desktop Tool 0개
```

첫 수직 Workflow는 다음 allowlist를 순서대로 실행한다.

1. `system.get_status.v1` — R0, read-only observation
2. `file.get_metadata.v1` — R0, read-only observation
3. `system.show_notification.v1` — R1, immutable prepared effect와 one-shot ticket

Connection Profile과 외부 typed executor는 포함하지 않으며 D-4 범위다.

## 완료 조건 증거

| 완료 조건 | 구현 및 결정적 증거 |
|---|---|
| 3개 이상 Tool step의 sample Workflow 실행 | `DesktopWorkflowToolExecutorTests.ExecutesThreeDesktopToolsAndConsumesNotificationTicketOnce`가 두 observation과 한 notification effect를 실행하고 step 원장, 단일 ticket 소비와 알림 1회를 확인한다. `DesktopWorkflowOrchestratorTests.RevalidatesFrozenWorkflowExecutesOnceAfterLaterActivationAndCoalescesReplay`는 1.0.0을 run에 고정한 뒤 2.0.0을 활성화해도 exact 1.0.0 정의를 재검증·실행한다. |
| 승인 뒤 payload/order/risk/target 변경 거부 | validated plan의 definition/input-contract/plan hash와 runtime input/authorization hash, generated Tool input/result schema를 실행 직전 다시 결합한다. `RejectsSelfConsistentForgedNotificationMetadataBeforePolicyOrEffect`와 atomic reservation failure 회귀는 조작 또는 저장 실패 때 policy/effect 0회를 확인한다. |
| Sidecar 단절과 duplicate에서 effect 중복 방지 | 실행 중 exact duplicate는 같은 in-memory task에 합류하고, 종료 뒤 duplicate는 schema 14 terminal row를 재생한다. ticket item과 step reservation CAS가 함께 성공한 뒤에만 effect를 시작한다. startup recovery는 observation을 `interrupted`, 시작한 effect를 `outcome_unknown`/`attention_required`로 고정하고 active ticket을 revoke한다. Sidecar bridge는 disconnect 즉시 pending 호출을 실패시키고 late result를 받지 않는다. |
| 실패 step과 다음 행동을 Activity UI에 표시 | 기존 최근 활동 조회가 Workflow run을 함께 투영한다. 마지막 유효 실패 attempt의 step ID/Tool과 `provide_information`, `retry_from_start`, `review_outcome`을 사용자 문구로 표시하며 `needs_information`, `running`, `attention_required`, `interrupted` 상태를 구분한다. `WorkflowActivityTests`가 실패 단계, 다음 행동, 원문 비저장과 상태 label을 확인한다. |

## 계약과 Sidecar 경계

- Protocol 1.19의 생성 계약 hash는 `b92843376eb07e0a2af0b1472af9532124c399283112a9d5fd0f84b016763571`이다. Desktop과 Sidecar가 같은 생성물을 사용한다.
- handshake는 `workflow.invoke`, `workflow.result`, `workflow.runtime.v1`을 필수로 요구한다. 실제 dedicated bridge가 wiring된 runtime만 `workflow.runtime.v1`을 광고한다.
- selector call ID `wfsel_<48hex>`, invoke call ID `wfinv_<48hex>`와 idempotency key `wfid_<64hex>`는 원래 Cline Tool call identity에서 서로 다른 format hash로 만든다. 모델 입력으로 받지 않는다.
- `workflow.invoke.v1`에는 parent conversation/run, exact `CapabilityInvocationContext`, idempotency key, Desktop deadline과 32 KiB 이하 input만 포함한다. 정의, step, ticket과 nonce는 IPC에 없다.
- terminal 상태와 다음 행동은 `succeeded/none`, `needs_information/provide_information`, `failed/retry_from_start`, `cancelled/retry_from_start`, `attention_required/review_outcome`의 exact 조합만 허용한다.
- Sidecar는 최종 `result` object 자체에 UTF-8 **정확히 100 KiB(102,400 bytes)**, depth 16, node 4,096 상한을 적용한다. 전체 result envelope에는 summary, 상태와 pointer metadata를 위해 32 KiB, depth 1, node 64의 별도 여유만 둔다. 따라서 envelope 상한은 132 KiB/depth 17/node 4,160이고 이 여유가 final output 예산을 늘리지는 않는다.
- `needs_information`은 1~32개의 unique RFC 6901 pointer를 요구하며 각 pointer는 최대 512자다.
- 생성된 C# record는 optional `summary`, `requiredInputs`, `result`, `errorCode`, outer `output`과 `error`에 `JsonIgnoreCondition.WhenWritingNull`을 적용한다. `GeneratedCapabilityContractTests.WorkflowResultOmitsAbsentOptionalPropertiesOnTheWire`가 JSON의 absent와 `null`을 구분한다.
- admission/context/policy 거부는 outer `success=false`와 고정 `error`다. 원장에 시작된 Workflow의 업무 terminal은 outer `success=true`의 구조화 output으로 반환한다.

## Desktop-owned 검증과 실행

- `.ligcap`의 `workflow` kind는 exact `workflow.json` entrypoint를 요구한다. prompt package의 Workflow entry, executable/nested archive/symlink, case collision, secret literal과 size 초과를 import 전에 거부한다.
- activation과 invocation 직전에 같은 `WorkflowDefinitionParser`와 `WorkflowStaticValidator`를 실행한다. 최대 20 step, branch depth 5, 뒤쪽 edge만 허용하며 cycle/dangling/unreachable, case collision, 잘못된 JSON Pointer, 선언 Tool/risk drift와 meta/shell을 fail-closed 한다.
- `ValidatedWorkflowPlan`에는 definition/input/plan hash, exact canonical Tool, canonical risk, preparation/retry/effect class와 generated input/result contract hash가 포함된다.
- parent run의 frozen snapshot authorization이 exact capability ID/version/content hash/catalog revision/model Tool을 고정한다. Orchestrator는 그 exact immutable version을 다시 읽고 content hash와 Workflow를 재검증한다. 이후 다른 version이 활성화돼도 현재 run의 정의는 바뀌지 않는다.
- observation은 기존 `ToolInvocationCoordinator`와 Windows Tool policy를 사용하며 `read_only`만 같은 in-flight process에서 bounded retry할 수 있다.
- notification effect는 immutable payload/binding hash와 expiry를 가진 `IPreparedWorkflowEffect`로 복사한다. `AllowOnce` 외 대화/30일 grant는 거부하며, effect step마다 single-item ticket을 만든다.
- 저장소가 주입받은 `TimeProvider`가 ticket 만료 판단의 권위다. Sidecar/호출자가 보낸 과거 `StartedAtUtc`로 expiry를 되돌릴 수 없다.

## Durable ledger, duplicate와 복구

SQLite schema 14는 다음 metadata-only table을 Desktop 단독 소유로 추가한다.

- `workflow_runs`
- `workflow_authorization_tickets`
- `workflow_authorization_ticket_items`
- `workflow_step_runs`

원장에는 exact Capability context와 workflow/input/plan/result hash, risk, sequence/attempt, fixed status/error/next action과 redacted summary만 저장한다. prompt, Workflow input, Tool input/output, 파일 내용, credential과 token은 저장하지 않는다.

- `(parent_run_id, idempotency_key)`와 `(parent_run_id, parent_tool_call_id)`는 unique다. 같은 key/call의 exact identity와 input만 합류하고 다른 identity/input은 `workflow.idempotency_conflict`다.
- 실행 중 exact duplicate는 `ConcurrentDictionary`의 exact identity/hash key로 같은 `Task<WorkflowResultParams>`를 기다린다. 첫 호출이 끝나기 전에 `workflow.run.in_progress`를 잘못 반환하지 않는다.
- 종료 뒤 exact duplicate는 terminal row를 재생한다. `needs_information`의 bounded `required_pointers_json`도 함께 저장해 같은 `requiredInputs`를 복원한다.
- step은 strict serial sequence와 revision CAS를 지킨다. effect는 `planned → executing`과 ticket item `ready → consumed`가 한 transaction에서 성공하기 전에는 시작하지 않는다.
- app startup은 schema 초기화 뒤 recovery를 store instance당 한 번 실행한다. 사용하지 않은 ticket/item을 revoke하고, 실행 중 observation은 `interrupted`, 실행을 시작한 effect는 `outcome_unknown`, half-terminal run은 canonical next action과 completion timestamp로 마무리한다. 자동 effect 재실행은 없다.

## 취소와 Sidecar disconnect

- Workflow CTS는 parent conversation run token과 Desktop deadline에 연결된다. Sidecar disconnect 상태를 받으면 UI가 active run을 즉시 종료하고 `ConversationSessionState.EndRun`이 parent token을 취소한다.
- effect reservation 전 취소는 `cancelled/retry_from_start`로 정리할 수 있다. effect가 시작됐거나 결과를 확정할 수 없으면 `attention_required/review_outcome`으로 승격하고 자동 재실행하지 않는다.
- dedicated Sidecar bridge의 dispose는 pending Workflow promise를 즉시 실패시키며 duplicate call ID 재사용과 late result를 거부한다. 이미 기록된 Desktop terminal ledger는 결과 전달 실패로 되돌리지 않는다.
- timeout 순서는 Desktop deadline 8분 45초, bridge 10분, AgentTool 11분으로 두고 Capability 선택 30초와 bridge를 합친 시간도 AgentTool 안에 들어오게 한다.

## 표적 검증 증거

이번 D-3 작업에서 다음 결정적 표적 회귀를 확인했다.

- Sidecar: Workflow projection/bridge, 실제 Cline zero-Tool next turn, exact ID/timeout, 100 KiB/depth/node 경계, envelope smuggling과 schema parity
- Contracts: Protocol 1.19 metadata, generated Tool schema pair, Workflow optional JSON omission
- Application: parser/static validator, input/transform/condition/needs-information, schema/hash/byte bound, effect 뒤 failure/cancel의 attention 승격
- Desktop package/catalog: Workflow `.ligcap` round trip, kind-specific entry, activation 직전 동일 static validation
- Desktop executor/orchestrator: 3-Tool fixture, forged metadata와 reservation failure effect 0회, exact frozen version, in-flight/terminal coalescing, durable needs-information replay
- SQLite: schema 13→14 migration, strict order/CAS, trusted-clock expiry, one-shot startup recovery, half-terminal finalization과 원문 비저장
- UI: Workflow Activity 실패 step/next action과 상태 label 5개 표적 case

2026-08-02 저장소 루트의 `./scripts/verify.ps1`이 통과했다.

- Sidecar: 123
- Contracts: 16
- Application: 158
- Sidecar Integration: 1
- Desktop: 529
- .NET build: warning 0, error 0

`npm audit`은 Cline의 미사용 `dify-ai-provider` 하위 `@ai-sdk/provider-utils` 경로의 기존 low advisory 1건을 보고했다. verify exit는 0이며 호환성 없는 transitive 강제 변경이나 lockfile 변경은 적용하지 않았다.

`./scripts/smoke-ui.ps1`도 통과했다. IntegratedPages 6, TopLevelWindows 1, CompactLayout 760×500과 focus/validation/sorting/conversation boundary/keyboard/Precision Workspace 검사를 모두 통과했고 ApplicationErrors는 0이다. Workflow Activity의 실패 step·next action 내용은 위 `WorkflowActivityTests`가 결정적으로 검증하고, 이 smoke가 실제 통합 Activity 페이지의 UIA 접근·compact layout 경로를 닫는다.

## 남은 비차단 범위

- D-3는 같은 run의 crash 이후 resume를 지원하지 않는다. 실행 중 exact duplicate만 같은 in-memory task에 합류하고, terminal replay만 durable하다. restart 뒤 새 실행은 새 run/승인이며 effect 결과가 불명확하면 먼저 사용자가 검토해야 한다.
- first slice ticket은 notification effect마다 하나의 item만 발급한다. 여러 effect의 batch 승인 UX는 immutable target binding을 갖춘 adapter와 별도 검토 뒤 확장한다.
- Connection Profile, HTTP/SQL/mail/Git executor와 secret binding은 D-4 범위다.
