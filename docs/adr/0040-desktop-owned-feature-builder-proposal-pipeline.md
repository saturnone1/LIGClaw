# ADR 0040: Desktop-owned Feature Builder proposal pipeline

> 상태: 채택
> 기준일: 2026-08-02
> 관련 계획: `../D_STAGE_PLAN.md`
> 인수 증거: `../D5_ACCEPTANCE.md`

## 맥락

LIGClaw는 사용자의 자연어 요구를 기존 typed Tool과 선언형 Workflow로 조합한 Capability로 만들 수 있어야 한다. 이 생성 과정에는 Cline 기반 요구사항 수집과 후보 작성이 필요하지만, 모델이 Capability Catalog, SQLite, Windows 효과, secret 또는 활성화를 직접 소유해서는 안 된다.

기존 Node Sidecar를 .NET으로 옮기면 현재 사용하는 `@cline/agents` 런타임을 그대로 사용할 수 없다. 따라서 Cline이 필요한 replaceable agent runtime은 Sidecar에 유지하고, 신뢰 결정과 모든 durable mutation은 기존 Desktop 경계에 남겨야 한다.

Feature Builder 산출물은 model-authored untrusted input이다. 정상 JSON처럼 보이더라도 package path/media/크기, 중복 property, Tool·Connection inventory, Workflow 구조, shell·connection literal, secret literal과 canonical hash를 Desktop이 다시 검사해야 한다. 검사에 실패한 raw 산출물을 SQLite나 로그에 보존하는 것도 허용할 수 없다.

또한 “생성안을 적용”하는 동작과 “실행 가능한 버전을 활성화”하는 동작은 분리해야 한다. Builder의 성공만으로 active version을 바꾸면 기존 Capability review, readiness, 승인과 activation history 경계를 우회하게 된다.

## 결정

### 프로세스 소유권

- Node Sidecar와 `@cline/agents`를 유지한다.
- Sidecar는 Protocol 1.21의 `capability.proposal.generate` RPC와 임시 Feature Builder agent만 소유한다.
- Feature Builder agent에는 callable Tool을 하나도 제공하지 않고 iteration을 1회로 제한한다. Sidecar는 파일, SQLite, Windows adapter, Connection secret, Capability activation을 호출할 수 없다.
- Desktop은 session/proposal/test ID, request·response·inventory·proposal·evidence hash, 상태 전이, SQLite, 정책, quarantine, test, draft와 activation 경계를 소유한다.
- Domain과 Application은 WPF, SQLite, Cline, MCP와 Windows adapter를 참조하지 않는다.

```text
사용자 요구사항
 → Desktop session + inventory snapshot
 → Sidecar의 zero-tool Cline proposal generation
 → exact correlation과 bounded response 검증
 → Desktop shared package materializer + static scanner
 → Desktop no-effect fixture dry-run
 → proposal/test evidence CAS
 → ReadyForReview draft 생성
 → 기존 Capabilities review에서 별도 활성화
```

### RPC와 생성 경계

- 요청과 응답은 schema-first generated C#/TypeScript 계약을 사용한다.
- 응답은 `schemaVersion`, `generationId`, `requestHash`, `sessionId`, `sessionRevision`, `inventoryHash`, `deadlineUtc`를 요청과 exact하게 echo해야 한다.
- 한 generation ID는 같은 request fingerprint에서만 idempotent하게 재사용한다. 다른 payload가 같은 ID 또는 request hash를 재사용하면 tamper로 거부한다.
- caller deadline은 최대 60초다. deadline Promise를 runtime run과 race하므로 `abort` 뒤에도 끝나지 않는 agent가 caller를 영구 대기시키지 못한다. 전체 response는 최대 1 MiB, artifact는 최대 6개·각 256 KiB이며 질문은 한 번에 1~3개다.
- 응답은 `needs_information`, `proposal`, `refused` 중 하나만 가질 수 있다. Desktop-owned ID/hash/parent/revision/status/activation field는 모델 응답에 허용하지 않는다.
- Sidecar에서 runtime failure와 model response 오류는 bounded fixed code로 축약하며 prompt, answer와 artifact 원문을 로그에 남기지 않는다.

### 질문과 secret

- 일반 답변만 session에 저장한다. question ID, prompt, choice와 answer는 문자·UTF-8 byte·개수 상한과 case-insensitive 중복 검사를 거친다.
- password, passphrase, API key, token, credential 또는 secret을 요구하는 질문과 그러한 값을 닮은 답변은 Sidecar와 Application 양쪽에서 거부한다.
- Capability 산출물에는 secret placeholder만 허용한다. 실제 secret 수집은 Feature Builder 대화가 아니라 Desktop-owned Connection/Credential 화면의 후속 typed slice가 담당한다.
- SQLite의 Feature Builder table에는 secret column을 두지 않는다.

### 산출물 admission과 quarantine

- Sidecar artifact는 Desktop의 `FeatureBuilderProposalMaterializer`를 거쳐 기존 `.ligcap` `CapabilityPayloadMaterializer`와 같은 canonical payload가 된다. 독립적인 느슨한 Builder parser를 두지 않는다.
- 허용 파일은 `manifest.json`, `instructions.md`, `input.schema.json`, 선택적 `settings.schema.json`, 선택적 `workflow.json`, `tests.json`뿐이다. path, media type, UTF-8, entry 수와 expanded byte 상한을 exact하게 검사한다.
- canonical materialization 뒤 Application scanner가 Capability definition, dependency inventory, fixture, Workflow, risk, shell/connection/secret ingress를 다시 검사한다. 두 materializer의 payload와 content hash가 다르면 fail-closed 한다.
- `tests.json`에는 최소 한 개의 bounded fixture가 필요하다.
- 실패한 제안은 `quarantined` metadata만 저장한다. candidate content, artifact 원문, test evidence와 draft ID는 모두 null이며 fixed issue code와 hash만 남긴다.

### 테스트와 적용

- 정적 검사는 persisted candidate를 다시 shared materializer/scanner에 투입하고 content hash까지 재확인한다.
- dry-run은 fixture input이 declared input schema에 대해 예상한 `accepted`/`rejected` 결과를 내는지만 검사한다. Windows Tool host, HTTP, Credential Manager 또는 다른 effect adapter를 호출하지 않는다.
- static scan과 dry-run의 최신 attempt가 모두 동일 candidate hash로 `passed`여야 apply가 가능하다. evidence hash는 Desktop이 결정적으로 계산한다.
- apply는 하나의 SQLite transaction에서 proposal을 `applied`, session을 `completed`로 전이하고 source `builder`의 `ReadyForReview` draft만 만든다.
- apply는 active version, activation history와 catalog revision을 변경하지 않는다. 실제 activation은 기존 Capability review/readiness/CAS 경로를 별도로 통과해야 한다.
- proposal/test identity와 terminal row는 SQLite trigger와 repository validation으로 immutable하다. process 종료 때 `generating` session과 `running` test는 재시작 시 fixed `interrupted` 상태로 복구한다.

### 저장과 UI

- SQLite schema 16은 `feature_builder_sessions`, `improvement_proposals`, `capability_test_runs`를 추가한다.
- durable row는 CSPRNG 기반 opaque ID, canonical hash, bounded 일반 입력, 상태·revision·fixed code와 timestamp만 저장한다.
- Capabilities 화면의 `새 기능 만들기`가 Builder를 열며 별도 rail item을 추가하지 않는다.
- UI는 요구사항, 최대 3개 질문, 안전한 candidate 요약, test 결과, apply/reject를 보여준다. raw artifact와 secret 입력은 표시하지 않는다.
- apply 후에는 “활성 상태가 아니며 Capabilities에서 별도 검토·활성화해야 한다”는 사실을 명시한다.

## 결과

- Cline SDK 호환성을 잃지 않으면서 Sidecar의 권한은 effect 없는 proposal generation으로 제한된다.
- 모델 출력이 곧 package나 active Capability가 되지 않고 Desktop의 기존 package·Workflow·policy 경계를 재사용한다.
- 잘못되거나 위험한 proposal은 원문을 durable store에 남기지 않고 격리된다.
- Builder 성공과 activation이 분리되어 사용자가 검토하지 않은 기능이 실행 후보가 되지 않는다.
- D-6 이메일 slice는 secret 수집과 SMTP/HTTP mail draft/send를 Desktop-owned typed Connection/Executor로 추가해야 하며, generic shell이나 Sidecar effect로 대체할 수 없다.
- `file.move.v1`은 현재 Workflow prepared-effect allowlist와 collection mapping에 포함되지 않으므로 Builder가 파일 일괄 정리를 생성할 수 있다고 주장하지 않는다. 별도 typed batch mutation slice 전까지 제안은 quarantine되고 shell로 우회하지 않는다.

## 검증

- `dotnet format LIGClaw.slnx --no-restore`와 표적 Application Feature Builder 40/40, Desktop Feature Builder 37/37, UI source 26/26가 통과했다.
- `./scripts/verify.ps1`은 Sidecar 135/135, Contracts 18/18, Application 243/243, Sidecar Integration 1/1, Desktop 645/645, .NET build warning/error 0으로 통과했다. PowerShell parse, release manifest와 evidence 검증도 통과했다.
- `npm audit`에는 verify를 실패시키지 않는 기존 transitive low severity advisory 1건이 남아 있으며 호환성 근거 없이 lockfile이나 Cline 의존성을 강제 변경하지 않았다.
- `./scripts/smoke-ui.ps1`은 IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500과 열거된 UIA 검사를 모두 통과했고 ApplicationErrors는 0이다.

## 대안

- Cline runtime 전체를 .NET으로 이전하는 방식은 `@cline/agents`를 그대로 사용할 수 없어 채택하지 않는다.
- Sidecar가 package 파일이나 SQLite row를 직접 쓰는 방식은 정책, quarantine, CAS와 audit 소유권을 깨므로 채택하지 않는다.
- 모델에게 Desktop Tool, MCP 또는 shell을 주어 생성 중 연결 테스트나 파일 조작을 수행하게 하는 방식은 effect 경계를 우회하므로 채택하지 않는다.
- raw model artifact를 진단 편의를 위해 DB나 로그에 보존하는 방식은 secret·사용자 원문 노출 위험 때문에 채택하지 않는다.
- Builder apply와 activation을 한 버튼·transaction으로 합치는 방식은 readiness와 기존 review를 우회하므로 채택하지 않는다.
- fixture dry-run에서 실제 Tool/HTTP 효과를 실행하는 방식은 test가 외부 상태를 바꿀 수 있으므로 채택하지 않는다. 실제 연결과 effect 검증은 각 typed executor의 전용 probe/test 경로에서 수행한다.
