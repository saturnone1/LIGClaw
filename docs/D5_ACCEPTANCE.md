# D-5 Desktop-owned Feature Builder acceptance

> 상태: 인수 완료 · 전체 verify/UI smoke 통과
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0040-desktop-owned-feature-builder-proposal-pipeline.md`, `adr/0046-desktop-owned-workflow-semantic-fixtures.md`
> 계약/저장소: proposal RPC Protocol 1.21 · 현재 handshake Protocol 1.24 · SQLite schema 21

## 현재 판정

D-5 Feature Builder의 첫 수직 slice는 Cline Node Sidecar를 유지한 채 Desktop-owned proposal pipeline으로 연결됐다. Sidecar의 임시 Builder agent는 Tool 0개와 iteration 1회로 요구사항을 질문 또는 package artifact 후보로 바꿀 뿐이다. Desktop이 correlation, secret, package canonicalization, Tool·Connection inventory, Workflow, static scan, fixture dry-run, durable 상태와 draft 생성을 다시 소유한다.

Builder의 “적용”은 활성화가 아니다. 검사 증거가 고정된 proposal은 source `builder`의 `ReadyForReview` draft만 만들고 active version과 catalog revision은 바꾸지 않는다. 사용자는 기존 Capabilities review/readiness/activation 경계를 별도로 통과해야 한다.

```text
사용자 요구사항
 → Desktop session과 frozen inventory hash
 → Sidecar zero-tool Cline generation
 → 1~3개 일반 질문 또는 bounded artifacts
 → Desktop shared .ligcap materializer와 static scanner
 → static scan + v1 schema fixture 또는 Observation-only v2 semantic dry-run
 → evidence hash와 proposal CAS
 → ReadyForReview draft
 → 기존 Capabilities 화면에서 별도 검토·활성화
```

## 완료 조건별 구현 증거

| 완료 조건 | 구현 및 결정적 증거 |
|---|---|
| Cline 호환성과 프로세스 경계 | `ClineAgentRuntimeAdapter.createFeatureBuilderAgent`는 기존 `@cline/agents` provider/model을 재사용하되 `tools: []`, `maxIterations: 1`인 임시 agent만 만든다. `SidecarSupervisor`는 generated RPC를 `IFeatureBuilderRuntimeClient`로 변환할 뿐 Desktop ID/hash/state를 Sidecar에 넘기지 않는다. |
| schema-first proposal RPC | Protocol 1.21에서 도입된 `capability.proposal.generate`는 generation/request/session revision/inventory/deadline correlation을 exact echo한다. 생성 계약 hash는 `22cf1ce2455dcb2c495b40c320e095a14d237d15a6a478a0899c0fc383355f5f`이며 C#과 TypeScript generated output이 같은 값을 가진다. 현재 required capability handshake와 release manifest evidence는 실제 구현 순서에 따른 Protocol 1.24/schema 21을 고정한다. |
| bounded generation과 hard deadline | request deadline은 최대 60초, response 1 MiB, artifact 최대 6개·각 256 KiB, 질문 최대 3개, cache 최대 32개·10분이다. generation ID 재사용은 request hash와 전체 fingerprint가 같을 때만 idempotent하며 drift는 tamper로 거부한다. deadline Promise가 `AgentRuntime.run()`과 race하므로 abort에 협조하지 않고 영원히 pending인 runtime도 caller deadline에 fixed `capability.proposal.deadline_exceeded`로 끝나며 timer와 subscription을 정리한다. |
| missing-information 상태 | session은 `collecting → generating → awaiting_information → generating → proposal_ready`를 revision CAS로 진행한다. 한 turn 질문은 1~3개이며 이미 답한 ID 재질문과 누적 16답변 상한을 넘기는 질문은 generation을 `interrupted`로 종결한다. 질문 대기 중 취소는 pending questions를 지우고 terminal `cancelled`로 저장한다. |
| 대화 secret 차단 | requirement/answer/question/choice/limitation에 문자·UTF-8 byte·개수 상한을 적용한다. password/API key/token/credential을 요구하는 prompt와 민감 question ID, raw/JWT/Bearer/assignment 형태의 값은 Sidecar와 Domain/Application 양쪽에서 거부한다. Builder UI에는 secret 입력이나 raw artifact 표시가 없으며 secret은 후속 Desktop Connection/Credential 화면에서만 수집한다. |
| shared package admission | `FeatureBuilderProposalMaterializer`는 허용 path/media/strict UTF-8/entry·expanded byte 상한을 검사한 뒤 기존 `CapabilityPayloadMaterializer`로 canonical payload를 만든다. Desktop scanner가 canonical entries를 Application scanner에 다시 넣고 manifest/schema/tests/Workflow/dependency/risk/secret/connection/shell policy와 content hash parity를 확인한다. Builder 전용 느슨한 package parser는 없다. |
| fixture 필수와 무효과 test | 모든 proposal은 bounded fixture를 하나 이상 가져야 한다. v1 `{name,input,expected}` 배열은 Prompt와 effect Workflow에서 기존 input schema `accepted`/`rejected` 의미를 그대로 유지한다. semantic v2는 exact `{schemaVersion:2,cases:[...]}`이며 actual `WorkflowInterpreter`를 deterministic fake result로 실행해 ordered branch trace, step/Tool identity, canonical argument hash, terminal status/output hash를 비교한다. static metadata가 `Observation + PreparationMode.None`인 Workflow만 허용하고 Effect/prepared Tool은 simulation 전과 fake executor에서 이중 거부한다. Windows Tool host, HTTP, Credential, process와 effect resolver dependency는 0개다. 최신 static scan과 dry-run이 같은 candidate hash로 모두 passed여야 apply할 수 있다. |
| metadata-only quarantine | media mismatch, expanded size 초과, secret literal, connection literal, shell/script literal, semantic fixture의 raw credential과 missing tests는 `quarantined`로 종결한다. candidate hash와 여섯 artifact column, evidence와 draft ID는 null이고 fixed issue code만 남는다. 실제 DB/WAL/SHM canary 회귀는 unsafe raw artifact occurrence 0건을 확인한다. admission을 통과한 일반 fixture 원문은 content hash와 review 재현에 필요한 package 정의이므로 proposal/draft/version payload에 저장되지만, test result ledger에는 case-name hash·count·pass·fixed code만 저장하고 input/argument/fake output/terminal output 원문은 복제하지 않는다. |
| durable 상태와 crash 복구 | SQLite schema 21의 `feature_builder_sessions`, `improvement_proposals`, `capability_test_runs`는 Desktop-issued bounded ID, revision, canonical hash, bounded 일반 값, fixed code와 UTC timestamp를 저장한다. schema 21은 proposal `tests_json` root를 기존 v1 array 또는 strict `schemaVersion=2` object로만 확장하며 case 의미는 Application이 재검증한다. 20→21 migration은 실제 proposal/test row와 hash/status, FK, index 2개와 immutable trigger 7개를 보존하고 reopen·failure rollback으로 고정된다. proposal payload/evidence/terminal row와 test identity/terminal row는 trigger와 repository validation으로 immutable하며 startup은 `generating` session과 `running` test를 한 번만 `interrupted`로 복구한다. |
| review-only apply | repository는 동일 candidate의 최신 static/dry-run evidence를 transaction 안에서 다시 읽고 hash를 검증한다. 성공하면 proposal=`applied`, session=`completed`, draft=`ReadyForReview`를 원자적으로 만들며 active version과 catalog revision은 그대로 둔다. duplicate/stale/evidence mismatch는 draft·activation mutation 없이 fail-closed 한다. |
| 실제 typed Workflow 수직 회귀 | effect 예시인 `system.get_status.v1 → system.show_notification.v1 → output`은 호환 v1 accepted/rejected fixture만 사용해 생성 중 알림을 실행하지 않고 `ReadyForReview` draft가 된다. 별도의 `file.get_metadata.v1 → condition → output` Observation-only proposal은 v2에서 actual interpreter를 실행해 성공하고, wrong branch/binding/Tool/argument/status/output은 모두 실패한다. effect Workflow가 v2를 가장하면 `feature_builder.semantic.effect_forbidden`으로 draft 전환이 막힌다. |
| WPF와 760×500 경계 | Capabilities의 `새 기능 만들기`가 별도 rail item 없이 Builder를 연다. 요구사항→질문→안전한 manifest/dependency/위험/제한 요약→검사→apply/reject를 한 ScrollViewer에서 제공하고 apply 후 별도 activation 필요를 명시한다. UIA smoke는 일반/760×500에서 Builder 진입, 요구사항 ValuePattern 입력, 생성 버튼 InvokePattern 지원만 확인하고 실제 모델 호출 없이 복귀한다. |

## 실제 D-5 인수 시나리오

```text
“시스템 상태를 확인한 뒤 완료 알림을 보여 주는 기능을 추가해”
 → “알림 제목은 무엇인가요?”
 → “간단한 알림/상세한 알림 중 무엇인가요?”
 → system.get_status.v1 + system.show_notification.v1 Workflow proposal
 → Desktop static scan + effect를 실행하지 않는 v1 schema fixture dry-run
 → source=builder ReadyForReview draft
 → active version은 그대로
 → 사용자가 Capabilities 검토 화면에서 별도 활성화
```

이 시나리오는 Feature Builder가 기존 typed Tool을 선언형 Workflow로 조합하고도 생성·test 중 효과를 실행하지 않으며 activation을 우회하지 않는 최소 수직 증거다. 의미 실행 증거는 별도의 Observation-only file metadata v2 fixture가 담당하고, 알림 같은 effect Workflow는 v2로 위장할 수 없다.

## 의도적으로 완료로 주장하지 않는 범위

- “지정 폴더의 파일을 유형별로 정리”는 현재 Workflow v1에 collection mapping과 `file.move.v1` prepared one-shot adapter가 없으므로 D-5 성공 사례가 아니다. generic shell이나 model-direct file effect로 우회하지 않는다. 전용 batch file mutation 계약, preview, immutable prepared effect, approval/ticket와 replay를 갖춘 별도 수직 slice가 필요하다.
- Builder UI의 첫 slice는 새 Capability 생성 경로다. 기존 active Capability 수정과 실제 이전 버전 diff UI는 Domain/Application의 revision target 경계를 바탕으로 후속 improvement/revision 화면에서 연결한다.
- secret 입력, Connection probe와 실제 external effect test는 Builder 대화가 아니라 각 Desktop-owned typed Connection/Executor 화면이 담당한다.
- semantic v2의 deterministic fake result는 production `WorkflowInterpreter`의 branch/binding/argument/output 의미를 검증하지만 실제 Desktop Tool adapter, 파일 존재 여부, 네트워크 연결 또는 external effect 성공을 증명하지 않는다. 실제 adapter 회귀와 readiness/probe/activation은 계속 별도 경계다. v1은 기존 input-schema acceptance만 검증한다.

## 최종 검증 게이트

기존 D-5 인수 baseline은 아래 전체 게이트를 통과했다. semantic v2/schema 21 보강은 그 위에 focused 결정적 회귀를 추가하며, 현재 작업 트리의 전체 `verify.ps1` 결과와 혼동하지 않는다.

- `dotnet format LIGClaw.slnx --no-restore`: 성공
- 표적 회귀: Application Feature Builder 40/40, Desktop Feature Builder 37/37, UI source 26/26
- `./scripts/verify.ps1`: Sidecar 135/135, Contracts 18/18, Application 243/243, Sidecar Integration 1/1, Desktop 645/645, .NET build warning/error 0. PowerShell parse, release manifest와 evidence 검증도 통과했다.
- `npm audit`: verify를 실패시키지 않는 기존 transitive low severity advisory 1건을 계속 추적한다. 호환성 근거 없이 lockfile이나 Cline 의존성을 강제 변경하지 않았다.
- `./scripts/smoke-ui.ps1`: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500과 스크립트에 열거된 UIA 검사를 모두 통과했고 ApplicationErrors는 0이다.

semantic v2/schema 21 focused 증거:

- Application `CapabilityFixtureTestPolicyTests`: v1 호환, v2 exact shape와 case/tool/trace/value bound 검증
- Desktop `FeatureBuilderDesktopPipelineTests`: interpreter 성공, wrong branch/binding/Tool/argument/status/output, effect/prepared fail-closed, raw ledger·quarantine 검증
- Desktop `FeatureBuilderPersistenceSchemaTests`: 20→21 row/hash/status/FK/index/trigger 보존, reopen, 실패 transaction rollback과 strict v2 root 검증
- Sidecar `capability-proposal-generator.test.js`: Observation-only semantic v2와 effect/non-None v1 fallback prompt 경계 검증
