# ADR 0046: Desktop-owned Workflow semantic fixture와 schema 21 저장 경계

> 상태: 채택
> 기준일: 2026-08-02
> 관련 계획: `../D_STAGE_PLAN.md`
> 인수 증거: `../D5_ACCEPTANCE.md`
> 선행 결정: `0040-desktop-owned-feature-builder-proposal-pipeline.md`

## 맥락

기존 `tests.json` v1은 `{name,input,expected}` 배열로 입력 schema의 accepted/rejected만 검사한다. 이 형식은 Prompt와 effect를 포함한 Workflow에서도 안전하게 쓸 수 있지만, Workflow의 분기, binding, Tool 이름과 인자, step 결과 결합, terminal output 회귀를 찾지는 못한다.

의미 검증을 위해 실제 Windows Tool host나 HTTP·Credential·process adapter를 호출하면 Builder test 자체가 외부 상태를 바꿀 수 있다. 반대로 별도 간이 Workflow evaluator를 만들면 production `WorkflowInterpreter`와 의미가 달라질 수 있다. model-authored fixture의 입력·가짜 결과를 test ledger나 로그에 복제하는 것도 허용할 수 없다.

또한 schema 16에서 만들어진 `improvement_proposals.tests_json` CHECK는 JSON array만 허용했다. semantic v2의 versioned object를 저장하려면 기존 proposal과 test-run FK, index, immutable trigger를 잃지 않는 migration이 필요하다.

## 결정

### v1 호환과 semantic v2 형식

- 기존 v1 accepted/rejected 배열은 그대로 허용하고 의미를 바꾸지 않는다.
- semantic v2는 exact root `{schemaVersion:2,cases:[...]}`만 허용한다.
- 각 case는 exact `{name,input,toolResults,expected}`이고, Tool result는 exact `{stepId,tool,arguments,output}`, expectation은 exact `{status,output,visitedSteps}`다.
- case는 1~16개, case당 Tool result와 visited step은 각각 최대 20개다. input은 32 KiB, Tool arguments/result는 각각 64 KiB, terminal output은 100 KiB로 제한한다. 이름·step·Tool 형식, 중복·대소문자 충돌, JSON depth와 property ambiguity도 admission에서 거부한다.
- Sidecar Builder system prompt는 모든 proposal에 `tests.json`을 요구한다. 모든 `tool.call`이 정적으로 `Observation`이고 preparation이 `None`인 Workflow에만 semantic v2를 작성하도록 안내한다. Prompt와 Effect 또는 non-None preparation을 가진 Workflow는 v1 schema fixture만 작성한다.

### production interpreter를 쓰는 무효과 simulation

- Desktop은 candidate Workflow를 다시 parse하고 static validate한 뒤 production `WorkflowInterpreter`를 그대로 실행한다.
- static metadata가 `EffectClass.Observation`과 `PreparationMode.None`을 모두 만족하지 않는 Tool step이 하나라도 있으면 `feature_builder.semantic.effect_forbidden`으로 simulation 전에 거부한다. fake executor도 같은 조건을 다시 검사한다.
- fake executor는 fixture 순서대로 exact step ID와 canonical Tool name을 확인하고, 실제 evaluated arguments와 fixture arguments의 canonical SHA-256 hash를 비교한다. fixture output은 해당 Tool의 exact result schema/hash를 통과한 경우에만 interpreter에 돌려준다.
- 실행 후 ordered visited step, terminal status와 canonical terminal output hash를 비교한다. wrong branch, binding, Tool name, argument, terminal status와 output은 모두 fixed `feature_builder.semantic.fixture_mismatch`로 실패한다.
- simulator의 dependency는 정적 `IWorkflowToolMetadataCatalog`뿐이다. Windows Tool host, HTTP client, Credential Manager, process, connection/effect resolver를 참조하거나 호출하지 않는다. 실제 adapter 동작과 연결 readiness는 각 typed Tool의 전용 회귀·probe가 계속 담당한다.

### raw fixture와 ledger의 서로 다른 저장 경계

- admission을 통과한 canonical `tests.json` 원문은 manifest·workflow와 같은 Capability/Builder proposal 정의의 일부이므로 `improvement_proposals`, draft와 version payload에 저장된다. 이는 재검토와 content hash 재현에 필요한 package content다.
- secret·credential처럼 허용되지 않는 raw 값이 fixture input/output에 들어오면 proposal 전체를 metadata-only quarantine하고 candidate artifact를 DB/WAL/SHM에 저장하지 않는다.
- `capability_test_runs.result_json`은 case 이름의 SHA-256, case/match count, pass boolean과 fixed error code만 저장한다. fixture input, evaluated arguments, fake Tool output, terminal output과 case 이름 원문은 ledger나 로그에 저장하지 않는다.

### SQLite schema 21

- schema 21은 `improvement_proposals.tests_json` root CHECK만 확장한다. 기존 valid array 또는 exact root key, integer `schemaVersion=2`, 1~16개 `cases` array인 object만 저장할 수 있다.
- case 내부 의미·byte bound·중복 검사는 SQLite에 복제하지 않고 Application의 `CapabilityFixtureTestPolicy`가 소유한다.
- migration은 FK child인 `capability_test_runs`를 먼저 rename하고 parent proposal table을 rename한 뒤 새 parent와 child에 모든 column을 explicit copy한다. old child, old parent 순서로 제거하고 원래 index 2개와 immutable trigger 7개를 exact하게 재생성한다.
- migration 전체는 기존 초기화 transaction 안에서 실행한다. 실패하면 schema version, row, index와 trigger 변경이 모두 rollback되며 다음 시작에서 재시도할 수 있다.

## 결과

- Workflow semantic fixture는 production interpreter의 분기·binding 의미를 검증하면서도 실제 효과를 0으로 유지한다.
- 기존 Prompt와 effect Workflow의 v1 package는 호환된다. effect Workflow가 v2를 가장해도 Desktop static metadata 경계에서 통과할 수 없다.
- proposal 정의에 필요한 canonical fixture와 test evidence ledger가 분리되어, 재현성은 유지하면서 실행 중 raw 값의 중복 저장을 막는다.
- schema 20→21 migration은 기존 proposal/test row, hash, status, FK, index와 immutable trigger를 보존하고 reopen과 failure rollback 회귀로 고정된다.

## 대안

- 별도 간이 Workflow evaluator는 production interpreter와 drift하므로 채택하지 않는다.
- 실제 Tool host나 Connection adapter를 dry-run에 주입하는 방식은 외부 효과와 credential 접근 가능성이 있어 채택하지 않는다.
- 모든 Workflow에 semantic v2를 강제하는 방식은 effect를 fake로 정상 처리했다고 오인하게 하므로 채택하지 않는다.
- fixture input/output 원문을 test result에 저장하는 방식은 package 정의와 실행 evidence의 목적을 섞고 민감 값 노출 면적을 늘리므로 채택하지 않는다.
