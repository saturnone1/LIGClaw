# D-2 Cline Capability Runtime Projection acceptance

> 상태: 완료
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0037-frozen-capability-run-snapshot-and-selector-handshake.md`

## 결과

D-2는 protocol 1.18의 schema-first `CapabilityRuntimeSnapshot`과 `capability.select.v1` handshake를 추가했다. Desktop은 active-only FTS와 immutable exact version으로 run별 후보를 고정하고, Sidecar는 이 bounded snapshot만 Cline `AgentTool`과 provider prompt에 투영한다. 지원 범위는 D-1과 같은 `prompt-v1`이며 SQLite schema는 13으로 유지한다.

Capability는 새 effect 경로가 아니다. 선택 뒤의 실제 Tool도 Desktop의 canonical risk, target 재검증, preview, 승인, timeout, at-most-once와 감사 경로를 그대로 통과한다.

```text
사용자 요청
 → Desktop active FTS + exact immutable version 조회
 → 최대 8개 후보 재검증·2 MiB snapshot 예산 적용
 → conversation.start
 → Sidecar hash/model Tool 재계산
 → Cline cap_* 후보 선택
 → capability.select.v1 exact handshake
 → selected instructions를 prepareTurn에만 투영
 → required Tool + exact CapabilityInvocationContext
 → Desktop Prepare/Claim + Capability authorization
 → 기존 ToolInvocationCoordinator·WindowsToolHost 실행
```

## 완료 조건 증거

| 완료 조건 | 구현 및 결정적 증거 |
|---|---|
| Cline deterministic model이 후보 Capability를 선택하고 기존 Desktop Tool 실행 | `D2CapabilityProjectionE2ETests.ClineSelectsFrozenCapabilityAndExecutesDesktopToolThroughPolicyPipeline`가 실제 Sidecar process와 Named Pipe, run/controller/coordinator/WindowsToolHost를 연결해 selector 시 effect 0회, `system.get_status.v1` 정확히 1회와 terminal 결과를 검증한다. Sidecar 실제 Cline loop 회귀도 selector와 exact-context Tool 호출을 고정한다. |
| 비후보·stale·비허용 Tool 거부 | Sidecar의 non-candidate, acknowledgement mismatch, same-turn race와 fallback 회귀, Application frozen authorization, Desktop selector parsing·context·allowlist·risk 회귀가 양쪽 경계를 독립적으로 검증한다. |
| activation 변경이 현재 run에 영향 없음 | snapshot과 candidate를 깊은 복사해 Desktop run authorization에 한 번만 bind한다. Application snapshot mutation과 Sidecar frozen projection 회귀가 현재 run의 model Tool, instructions와 required Tools가 변하지 않음을 확인한다. |
| 다음 run에 새 exact version 반영 | catalog revision과 exact version이 snapshot hash/session key에 포함된다. rollback revision hash, snapshot 변경 시 bounded history restore와 session recreate 회귀가 다음 run 반영을 검증한다. 후보가 있는데 Desktop history가 없으면 같은 hash라도 새 runtime으로 이전 selector 결과를 제거한다. |
| 후보 하나의 dependency 실패가 전체 run을 깨지 않음 | Desktop readiness는 실제 MCP 연결 상태를 포함한 Tool availability를 사용하고, Sidecar는 현재 노출할 수 없는 required Tool 후보만 격리한다. 다른 후보와 일반 대화는 유지한다. |

## 계약과 경계

- `contracts/`를 단일 원천으로 `CapabilityRuntimeCandidate`, `CapabilityRuntimeSnapshot`, `CapabilityInvocationContext`, `capability.select.v1` input/result를 생성했다.
- D-2 transport의 candidate kind는 계약부터 `prompt` 하나로 제한했다. Workflow와 Connector discriminator는 해당 runtime이 생기는 단계에서 protocol과 함께 확장한다.
- Desktop snapshot builder는 exact payload/content hash, prompt-v1 schema 전체, Tool inventory·canonical risk, 후보 최대 8개와 실제 UTF-8 직렬화 2 MiB 상한을 다시 검사한다.
- Sidecar는 snapshot hash와 deterministic `cap_<24 lowercase hex>` 이름을 다시 계산하며 2 MiB 상한도 방어적으로 검사한다.
- Sidecar `beforeModel`, `beforeTool`과 scoped Desktop bridge가 같은 selected `requiredTools`를 강제한다. 선택 뒤 JSON text fallback은 비활성화한다.
- provider routing key와 snapshot hash를 Cline session key에 결합한다. catalog activation/rollback은 active run을 바꾸지 않고 다음 run/session에 반영한다.

## 보안 불변식

- SQLite, 후보 검색, snapshot 고정, selector 최종 승인과 Windows effect는 Desktop이 소유한다. Sidecar는 catalog나 secret 저장소를 읽지 않는다.
- selector는 `R0` control-plane이며 exact snapshot/id/version/content-hash/revision만 전달한다. model arguments, 사용자 요청과 instructions는 selector payload에 포함하지 않는다.
- instructions는 선택 성공 뒤 provider `prepareTurn`에만 들어가며 transcript, Desktop history, SQLite message와 diagnostic log에 추가하지 않는다.
- 후보가 있으면 legacy memory 단축 경로를 사용하지 않는다. 선택 뒤 모든 일반 Tool에는 exact `CapabilityInvocationContext`가 필요하다.
- 모든 Tool call ID는 최초 수신 시 `Prepare`로 예약되고 continuation에서 한 번만 `Claim`된다. malformed/stale/preflight denial 뒤 같은 ID를 고쳐 재실행할 수 없고, Capability 선행 거부도 고정 오류로 감사하며 effect는 0회다.
- 지속 R1 grant scope는 snapshot hash, model Tool name, capability ID/version/content hash와 catalog revision에 결합한다. 다른 버전이나 비-Capability 호출로 승계되지 않는다.
- snapshot 생성·검증 실패는 partial/stale snapshot이나 권한 확대 대신 empty snapshot 또는 고정 run failure로 끝난다.

## 검증 결과

저장소 루트에서 `./scripts/verify.ps1`과 `./scripts/smoke-ui.ps1`을 통과했다.

- Sidecar: 98
- Contracts: 11
- Application: 90
- Desktop: 488
- Named Pipe integration: 1
- build: warning 0, error 0
- UIA smoke: integrated pages 6, 760×500, application error 0

별도 D-2 표적 회귀로 Sidecar Capability projection 14개와 실제 Named Pipe 수직 E2E를 확인했다. contract generation check, .NET format check와 `git diff --check`도 통과했다.

`npm audit`의 기존 `@ai-sdk/provider-utils` low advisory 1건은 Cline transitive dependency로 추적하며 자동 major/transitive 변경을 적용하지 않았다.

## 남은 비차단 항목

- 2 MiB snapshot byte 예산과 별개로 실제 token 비용은 provider/model context window에 따라 달라진다. tokenizer-aware 예산과 사용자 안내는 후속 provider hardening에서 다룬다.
- selector용 별도 durable ledger는 추가하지 않았다. 실제 effect audit는 기존 경로를 유지하고 exact Workflow run/step provenance와 execution ticket은 D-3 schema 14에서 추가한다.
- Connection Profile과 secret binding은 D-4 범위다. D-2에서는 runtime dependency가 없으면 해당 후보만 노출하지 않는다.

## 다음 단계

D-3는 `adr/0038-desktop-owned-workflow-ledger-and-one-shot-execution-ticket.md`에 고정한 Desktop-owned Workflow parser/interpreter, immutable execution plan, one-shot authorization ticket, run/step ledger, crash·cancel·retry·resume와 `outcome_unknown` 정책부터 구현한다. Durable at-most-once 경계가 준비되기 전에는 prompt-only 제한을 풀거나 Workflow가 Sidecar에서 일반 Desktop Tool을 직접 실행하도록 만들지 않는다.
