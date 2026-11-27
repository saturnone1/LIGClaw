# D-7 범용 Capability 진입·활성화 수직 acceptance

> 상태: 구현 완료 · 현재 통합 작업 트리 전체 verify/UI smoke 인수 통과
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0045-model-triggered-feature-builder-control-plane.md`, `adr/0046-desktop-owned-workflow-semantic-fixtures.md`
> 현재 계약: Protocol 1.24 · contract hash `7a221a94e96ef061630a92382356761cea49acdf271d0503a915c31ccd013776`
> 현재 저장소: SQLite schema 21

## 현재 판정

D-7의 범용 기능 추가 수직은 구현됐다. 사용자는 composer의 명시적 동작으로 현재 요청을 Feature Builder에 넘길 수 있고, 일반 Cline agent도 메일 전용 Tool이 아니라 최소 control-plane Tool로 같은 Builder 진입을 요청할 수 있다. Builder가 만든 exact `ReadyForReview` draft는 Capabilities 검토 대상으로 즉시 포커스되며, 비메일 `system.get_status.v1` Workflow는 검토·활성화 뒤 다음 run의 새 snapshot에서 선택되어 Desktop Workflow Runtime과 기존 Tool 정책 경로로 실행된다.

증거의 수준은 분리한다. composer handoff는 760×500 WPF UI smoke, exact review focus는 WPF construction/source, 모델 진입은 Desktop controller·Shell ACK-first 회귀와 전용 실제 Sidecar process/Named Pipe deterministic fixture, 비메일 Builder→activation→execution은 fake Builder runtime과 실제 Application service·SQLite·orchestrator를 연결한 결정적 cross-layer integration으로 검증한다. 이 네 경계를 한 번에 통과한 단일 실제 UI/vLLM/Named Pipe E2E라고 주장하지 않는다.

제품 목표는 이메일 클라이언트가 아니라 **등록된 typed Tool을 조합해 LLM에 새 Capability를 추가하는 범용 플랫폼**이다. D-6의 Mail Gateway와 SMTP는 외부 effect, durable outcome ambiguity, Connection 복구를 검증한 text-only reference adapter로 동결한다.

현재 통합 작업 트리는 저장소 루트 `./scripts/verify.ps1`과 `./scripts/smoke-ui.ps1 Debug`를 모두 통과했다. 따라서 D-7 저장소 인수 gate는 완료다. 실제 사내 vLLM을 포함해 composer 또는 모델 진입부터 화면 조작까지 한 번에 수행하는 실기 자동화는 이 문서에서 완료로 과장하지 않는다.

## 완료한 범위

| 범위 | 현재 구현 의미 |
|---|---|
| 명시적 composer → Builder | composer의 `기능으로 만들기`는 현재 입력을 `FeatureBuilderPolicy`로 검증한 뒤 Builder에 prefill한다. 요청을 전송하거나 입력을 지우거나 Builder session/proposal 생성을 자동 시작하지 않는다. 빈 값, 길이 초과와 secret 의심 값은 원문을 상태·진단에 반사하지 않고 fail closed한다. |
| 모델 Tool → ACK → Builder | Protocol 1.24 `capability.builder.open.v1` input은 `intent=create|revise`뿐이다. Desktop은 요청 수락 시점의 exact active `(conversationId, runId)`에 저장된 `user_input`만 결합하고 requirement-free fixed-structure Tool result가 Sidecar에서 ACK된 뒤에만 Dispatcher에서 Builder를 연다. ACK 뒤에는 accepted identity와 run sequence, 명시적 cancellation tombstone, current conversation/run context를 확인하고 화면 생성 직전에 같은 authorization을 재검사한다. 정상 agent completion이 ACK보다 먼저 도착한 경우는 같은 current conversation에서 허용하지만 명시적 취소나 새 context는 거부한다. ACK 실패, stale/cancel, duplicate, non-R0, Capability context, 추가 payload와 정책 위반은 화면 이동 0회다. |
| 실제 Sidecar IPC | 실제 Sidecar process/Named Pipe deterministic fixture가 Builder-open Tool의 R0, exact conversation/run identity, intent-only input, `{accepted:true,intent:create}` ACK와 terminal round-trip을 검증한다. 실제 사내 vLLM을 실행하는 UI E2E는 아니다. |
| exact review handoff | Builder apply는 activation이 아니라 exact `CapabilityVersionRef`의 `ReadyForReview` draft를 만든다. `DraftReadyForReview` event가 Capabilities 목록을 새로 읽고 그 exact version을 선택·포커스하므로 사용자가 별도 검토와 활성화를 계속할 수 있다. |
| 범용 Builder 구성 | Builder 화면 진입은 Capability/Feature Builder 서비스만 요구한다. Mail Gateway와 SMTP 관리 서비스는 선택적 Connection UI에만 연결되고, 지원 connection kind inventory는 Application의 중앙 정책 목록을 사용하므로 Builder 수명주기가 메일 구현 유무에 종속되지 않는다. |
| 비메일 cross-layer integration | fake Builder runtime을 사용해 “현재 Windows 시스템 상태를 조회해서 간결한 상태 요약을 만드는 기능” 요구가 질문/답변, proposal, static scan과 dry-run, `ReadyForReview`, activation, 다음-run runtime snapshot, `capability.select.v1`, Workflow 실행, `system.get_status.v1` 1회와 durable Activity까지 통과한다. 활성화 전 snapshot에는 후보가 없고 활성화 뒤 catalog revision 1의 exact version만 나타난다. 이 test는 WPF, 실제 Cline/vLLM 또는 Named Pipe를 포함하지 않는다. |
| semantic fixture v2 | exact `{schemaVersion:2,cases:[...]}` fixture는 Observation/PreparationMode.None Workflow에만 허용한다. production `WorkflowInterpreter`를 deterministic fake result로 실행해 branch, binding, Tool, canonical argument hash, terminal status/output을 검사하되 Windows Tool, HTTP, Credential, process와 effect resolver는 호출하지 않는다. |
| SQLite schema 21 | schema 20→21은 `improvement_proposals.tests_json` root를 기존 v1 array 또는 strict semantic v2 object로 확장한다. proposal/test row, FK, index와 immutable trigger를 보존하며 raw fixture는 package 정의에만 남고 test result ledger에는 hash/count/fixed code만 저장한다. |
| 진단·개발 인증서 경계 | Desktop 진단은 raw exception message와 Sidecar stdout/stderr를 번들에 전달하지 않고 fixed/type-only 진단만 남긴다. MSIX 개발 검증은 사용자가 지정한 기존 code-signing 인증서의 private key·EKU·신뢰 chain만 읽어 확인하며 `Root`, `TrustedPeople`, `My` 저장소에 인증서를 생성·추가·삭제하지 않는다. |

## 두 진입점의 안전한 공통 경로

```text
명시적 composer 동작
  └─ 현재 입력 검증 ───────────────────────────────┐
                                                   │
일반 Cline agent                                   │
  └─ capability.builder.open.v1(intent only)       │
      └─ exact persisted run input                 │
      └─ requirement-free fixed result → ACK       │
                                                   ▼
                                       Feature Builder prefill
                                       (자동 생성·적용 없음)
                                                   │
                              질문 → proposal → 검사/dry-run
                                                   │
                                       ReadyForReview exact draft
                                                   │
                                  사용자 검토 → 별도 활성화
                                                   │
                                 다음 run의 frozen snapshot
                                                   │
                      Desktop Workflow Runtime → registered typed Tool
```

모델은 requirement, prompt 또는 Capability identity를 Builder-open payload에 실어 나르지 않는다. persisted 원문은 기존 conversation 저장과 짧은 process-memory handoff 밖의 Tool result, activity, audit, log 또는 diagnostic에 복제하지 않는다.

위 그림은 구현된 경계를 합친 제품 흐름이지 한 자동화 test의 호출 스택이 아니다. WPF 진입, model control-plane, 전용 Sidecar process/Named Pipe Builder-open round-trip과 아래 cross-layer integration은 각각 독립 회귀로 검증한다.

## 비메일 cross-layer integration의 보장 범위

`GenericCapabilityBuilderVerticalTests.NaturalLanguageBuilderActivatesAndRunsGenericR0WorkflowIntoActivity`는 fake `IFeatureBuilderRuntimeClient`와 실제 Feature Builder/Capability service, SQLite repository, runtime snapshot, selector, Workflow orchestrator와 Windows Tool host를 다음처럼 결정적으로 연결한다. 실제 Sidecar process, Named Pipe, Cline/vLLM과 WPF는 이 test에 들어오지 않는다.

1. 등록된 Desktop Tool inventory에서 `system.get_status.v1`이 R0 Observation/PreparationMode.None임을 확인한다.
2. Builder가 일반 질문 하나를 하고 답을 포함한 비메일 Workflow proposal을 만든다.
3. static scan과 required dry-run을 같은 candidate hash에 결합한다.
4. apply가 source `builder`의 exact `ReadyForReview` draft만 만들고 활성 후보를 만들지 않는지 확인한다.
5. 기존 Capability review service가 exact draft를 활성화하고 catalog revision을 증가시킨다.
6. 다음 snapshot이 활성 exact version을 포함하고 selector가 frozen context를 claim한다.
7. Desktop Workflow Runtime이 `system.get_status.v1`을 정확히 한 번 실행하고 승인 요청 0회로 terminal `succeeded`를 만든다.
8. workflow ledger와 Activity가 같은 Capability ID/version/content hash/snapshot hash의 완료 결과를 남긴다.

이 수직은 기존 typed Tool 조합과 다음-run hot reload를 증명한다. Builder가 새 executable, URL, SQL, Git command 또는 arbitrary executor를 생성한다는 증거는 아니다.

## 일반화된 effect·outcome·Connection 의미

Capability 정의는 기존 Desktop registry의 typed Tool과 logical Connection kind만 참조한다. 실제 effect, secret 주입, 승인, one-shot ticket, 결과 판정과 복구는 계속 Desktop adapter가 소유한다.

- Observation은 현재 `system.get_status.v1` 비메일 수직처럼 effect 없이 실행할 수 있다.
- Effect는 canonical preview와 기존 승인 정책을 거쳐 prepared one-shot adapter만 실행한다.
- 결과는 adapter가 정의한 `known success`, `known failure`, `outcome unknown/attention required` 의미를 보존하며 불명확한 외부 효과를 자동 반복하지 않는다.
- endpoint·credential·binding 문제는 Capability version을 자동 수정하지 않고 Connection save → probe → exact rebind/readiness로 복구한다.
- 정의 결함만 active version 기반 새 proposal → test → review → activation 또는 rollback 경로로 수정한다.

현재 이 외부-effect 의미의 reference는 D-4 fixed HTTP, D-6 fixed Mail Gateway와 text-only SMTP다. 메일 adapter가 플랫폼 수명주기 자체는 아니다.

## 집중 검증 증거

현재 변경에서 확인된 집중 증거는 다음과 같다.

- Sidecar Builder-open Tool/schema: 8/8
- generated Contracts와 schema parity: 17/17
- Application control-plane policy: 8/8
- Desktop controller·persisted run·중복/stale 경계 묶음: 43/43
- 최종 `CapabilityBuilderOpenControllerTests`: 9/9
- ACK-before-open, ACK 뒤 identity/sequence/explicit-cancel/current-context 재검사와 화면 생성 직전 authorization 재검사를 포함한 controller + `XamlResourceTests` combined filter: 37/37
- composer 명시적 handoff, exact draft focus와 760×500 construction/source 회귀 통과
- 실제 Sidecar process/Named Pipe에서 `capability.builder.open.v1`이 exact R0 identity와 intent-only payload로 왕복하는 integration 1/1 통과
- 비메일 `GenericCapabilityBuilderVerticalTests`의 fake Builder runtime→실서비스/SQLite→review→activation→next-run→Workflow→Activity cross-layer integration 통과
- semantic v2의 success와 wrong branch/binding/Tool/argument/status/output, effect/prepared fail-closed, raw ledger/quarantine 회귀 통과
- contract generation check, TypeScript check/build, PowerShell 5.1 parse와 scoped .NET format check 통과

이 수치는 선택된 집중 회귀 결과다. 현재 결합된 저장소 전체 결과로 합산하거나 역사적 전체 verify 수치와 섞지 않는다.

## 전체 인수 증거

이미 인수된 기준선은 그대로 유효한 역사적 증거다.

- D-5 baseline `./scripts/verify.ps1`: Sidecar 135/135, Contracts 18/18, Application 243/243, Sidecar Integration 1/1, Desktop 645/645, build warning/error 0
- D-5 UI smoke: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500, ApplicationErrors 0
- D-6B baseline `./scripts/verify.ps1`: Sidecar 138/138, Contracts 22/22, Application 399/399, Sidecar Integration 1/1, Desktop 750/750, build warning/error 0
- D-6B UI smoke: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500과 열거된 UIA 검사 통과, ApplicationErrors 0

Protocol 1.24, schema 21, generic vertical과 D-7 UX를 모두 포함한 현재 통합 작업 트리의 최종 결과는 다음과 같다.

- `./scripts/verify.ps1` exit 0: PowerShell 5.1 parse, release manifest/evidence, contract generation parity, TypeScript check/build 통과
- Sidecar 141/141, Contracts 24/24, Application 454/454, Sidecar Integration 1/1, Desktop 847/847
- .NET build warning 0, error 0
- release evidence가 Protocol 1.24, contract hash `7a221a94e96ef061630a92382356761cea49acdf271d0503a915c31ccd013776`, SQLite schema 21을 고정
- `./scripts/smoke-ui.ps1 Debug` exit 0: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500, composer→Builder handoff, keyboard focus/navigation, typed SMTP secret clearing과 ApplicationErrors 0 통과
- UIA 보호 입력은 foreground window와 target focus를 제한 시간 동안 함께 재확인해 스크롤 직후의 일시적 focus timing을 실제 제품 실패로 오판하지 않는다.
- `npm audit`의 기존 transitive low severity advisory 1건은 verify exit 0 상태로 추적하며 호환성 근거 없이 Cline lockfile을 강제 변경하지 않는다.

## 반드시 보존할 제한

- `revise`는 사용자가 고른 exact Capability/version/revision target이 아직 없으므로 `capability.builder.revise_target_required`로 거부한다. create로 자동 변환하지 않는다.
- Builder는 arbitrary executor, shell, PowerShell, DLL 또는 새 네트워크 protocol 구현을 생성하지 않는다. 이미 등록된 typed Tool을 선언형 Workflow로 조합한다.
- SQL read-only와 Git read-only typed slice는 아직 구현하지 않았다. URL/SQL/command 문자열을 generic HTTP나 shell로 우회하지 않는다.
- semantic v2는 production interpreter 의미를 검사하는 무효과 simulation이며 실제 파일·네트워크·외부 effect 성공을 증명하지 않는다.
- 모델 Tool과 composer handoff는 Builder를 prefill할 뿐 session 생성, proposal generation, apply 또는 activation을 자동 실행하지 않는다.
- 현재 비메일 실행 수직은 R0 `system.get_status.v1`이다. 외부 effect와 Connection 복구의 구현 증거는 D-4/D-6 reference adapter가 담당한다.
- 이메일 attachment, HTML, arbitrary header, runtime Mail Gateway↔SMTP fallback과 outcome-unknown 자동 재전송은 동결된 reference 범위 밖이다.

## 최종 판정

D-7의 현재 구현 범위와 저장소 인수 gate는 완료했다. 이후 기능 팩은 위 제한을 유지한 채 SQL read-only, Git read-only처럼 실제 수요가 있는 typed adapter를 독립 contract·risk·Connection·replay 수직으로 추가한다.
