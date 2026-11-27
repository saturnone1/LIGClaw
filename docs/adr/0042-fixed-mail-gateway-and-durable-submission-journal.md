# ADR 0042: fixed Mail Gateway와 durable submission journal

> 상태: 채택 · D-6B 인수 완료
> 기준일: 2026-08-02
> 관련 계획: `../D_STAGE_PLAN.md`
> 선행 결정: `0041-desktop-owned-mail-draft-and-delivery-semantics.md`

## 맥락

D-6A 실행 adapter는 메일 원문을 별도 저장하거나 보내지 않고 canonical draft hash만 만든다. 실제 발송을 추가할 때 기존 `connection.http.request.v1`을 POST나 임의 mapping으로 확장하면 Workflow가 endpoint, header, auth와 wire contract를 사실상 소유하게 된다. 반대로 일반적인 outbox처럼 평문 메일을 SQLite에 저장하면 DB, WAL과 backup에 실행 payload의 수신자·제목·본문이 추가 복제된다. 현재 Workflow는 재시작 시 one-shot ticket을 폐기하므로, 승인 후 crash 난 원문을 자동 발송하는 의미도 기존 D-3 경계와 맞지 않는다.

## 결정

### D-6B 단계 구분

- D-6B1은 Protocol 1.23 `mail.send.v1`, mail send 정책, `mail-api` 연결 기반과 **metadata-only durable submission journal**을 구현한다.
- D-6B2는 고정 Mail Gateway probe/submit/reconciliation adapter, R3 AllowOnce prepared effect, UI와 수직 acceptance를 구현한다.
- 재시작 후에도 발송할 수 있도록 암호화 원문을 보관하는 queue는 D-6B 완료 의미에 포함하지 않는다. 실제 운영 요구가 확인되면 DPAPI·retention·backup·재승인 semantics를 별도 ADR로 결정한다.

따라서 이 단계의 `outbox`는 deferred/offline delivery queue가 아니라, network 시작 전 stable identity를 기록하고 중복 submission과 불명확한 결과를 조정하는 durable journal을 뜻한다.

### schema-first send 경계

- `mail.send.v1` 입력은 `connectionSlot`, 앞선 draft 결과의 `draftHash`, `to`, `cc`, `bcc`, `subject`, `body`만 받는다.
- Desktop은 `MailDraftPolicy`로 원문을 다시 정규화하고 hash를 재계산한다. 입력 hash가 다르면 network와 outbox mutation을 시작하지 않는다.
- Workflow static validator는 `connectionSlot`을 manifest의 literal `mail-api` slot에 고정하고, `draftHash`가 지배하는 `mail.create_draft.v1` step의 exact `/draftHash` binding인지 확인한다.
- endpoint, method, path, header, auth, token/password, sender/from, profile ID, operation/idempotency key, retry, HTML과 attachment는 모델·Workflow 입력에 존재하지 않는다.
- 결과는 bounded `operationId`, `status`, `draftHash`뿐이다. `queued`는 Gateway 접수이고 `delivered`가 아니다.
- canonical metadata는 `R3 / OneShot / Never / Effect`다. 일반 Tool 호출, Sidecar direct registration과 conversation/persistent grant를 허용하지 않는다.

### 별도 `mail-api` Connection

- 기존 GET-only `http` profile과 table을 재해석하지 않는다. schema 17은 submission journal만 추가하고, 후속 schema 18에 병렬 typed Mail Gateway profile/probe/binding 저장소를 추가한다.
- profile metadata에는 canonical HTTPS base URL, 고정 ASCII sender, enabled/lifecycle, revision과 target hash만 저장한다.
- Bearer token 원문은 Windows Credential Manager에만 저장한다. package, Builder, model, Sidecar IPC, SQLite와 로그에는 저장하지 않는다.
- target hash는 base URL, sender, fixed Gateway contract v1과 Bearer auth 의미에 결합한다. endpoint·sender·credential revision 변경은 probe, binding, runtime snapshot과 기존 승인 identity를 stale로 만든다.
- production composition은 단 하나의 `ConnectionExecutionLeaseCoordinator`를 HTTP와 Mail 관리 서비스 및 실행 resolver에 함께 주입한다. 두 kind의 profile·credential·probe·binding mutation과 execution final resolution이 같은 cross-kind gate를 통과하므로 mixed aggregate를 계산한 뒤 다른 kind가 바뀌는 TOCTOU를 허용하지 않는다.

### 고정 wire contract

Desktop adapter만 다음 route와 payload를 안다.

```text
GET  /.well-known/ligclaw-mail-gateway/v1
POST /ligclaw-mail-gateway/v1/submissions
GET  /ligclaw-mail-gateway/v1/submissions/{operationId}
```

- probe는 contract version 1, 같은 idempotency key의 중복 억제와 operation 조회 가능성을 exact bounded JSON으로 확인한다.
- submit은 profile의 sender, normalized draft, Desktop이 생성한 operation/idempotency identity를 fixed JSON과 fixed header로 보낸다.
- response는 같은 operation ID와 `queued` 또는 신뢰 가능한 final `delivered`만 허용한다. receipt 원문과 자유 메시지는 폐기한다.
- redirect, proxy, cookies, Windows default credential, arbitrary header, compression과 generic URL mapping은 차단한다.

#### Gateway v1 exact wire

Gateway v1의 JSON은 UTF-8이고 아래 property 외에는 허용하지 않는다. probe 응답은 4 KiB, submit·reconcile 응답은 4 KiB로 제한한다. 숫자는 JSON 정수, boolean은 JSON boolean이어야 하며 문자열로 대체할 수 없다.

```json
{
  "contract": "ligclaw-mail-gateway-v1",
  "idempotencyWindowSeconds": 300,
  "duplicateSuppression": "same-key-same-operation",
  "operationLookup": true
}
```

probe는 HTTP 200과 위 exact body일 때만 성공이다. v1은 동일 idempotency key와 operation ID 조합을 최소 300초 동안 중복 억제하고, 그 시간 안에 operation 조회가 authoritative하다는 의미다.

submit body는 다음 exact shape다. property 순서는 의미가 없지만 Desktop serializer는 이 순서로 canonical UTF-8 bytes를 만든다. 전체 request body 상한은 64 KiB다.

```json
{
  "contract": "ligclaw-mail-gateway-v1",
  "operationId": "mgo_<lowercase-hex-64>",
  "idempotencyKey": "mik_<lowercase-hex-64>",
  "draftHash": "sha256:<lowercase-hex-64>",
  "sender": "sender@example.invalid",
  "to": ["to@example.invalid"],
  "cc": [],
  "bcc": [],
  "subject": "plain-text subject",
  "body": "plain-text body"
}
```

submit은 `Authorization: Bearer <Credential Manager token>`, body와 같은 `Idempotency-Key`, `Accept: application/json`, `Content-Type: application/json; charset=utf-8`, 고정 User-Agent와 `Connection: close`만 application header로 설정한다. `Host`와 `Content-Length` 같은 HTTP transport header는 URI와 canonical bytes에서 framework가 계산한다. probe와 reconcile에는 Bearer, Accept, 고정 User-Agent와 Connection close만 사용한다.

submit 성공과 reconcile의 found 결과는 HTTP 200 또는 202의 다음 exact shape다.

```json
{
  "contract": "ligclaw-mail-gateway-v1",
  "operationId": "mgo_<lowercase-hex-64>",
  "status": "queued",
  "draftHash": "sha256:<lowercase-hex-64>"
}
```

`status`는 `queued`, `delivered`, `failed` 중 하나다. `queued`와 `delivered`만 Tool 성공이며 request의 operation ID와 draft hash가 모두 exact match해야 한다. `failed`는 고정 local error로만 변환하고 원격 메시지는 받거나 보관하지 않는다. reconcile의 authoritative 미발견은 HTTP 200의 다음 exact shape만 허용한다.

```json
{
  "contract": "ligclaw-mail-gateway-v1",
  "operationId": "mgo_<lowercase-hex-64>",
  "status": "not_found"
}
```

POST 뒤 redirect, non-success status, timeout, cancellation, transport failure, oversized·compressed·잘못된 content type·malformed JSON·identity mismatch는 그 응답만으로 known failure라 판단하지 않고 같은 operation ID를 한 번 조회한다. 조회도 authoritative exact body를 주지 못하면 `outcome_unknown`이다. 초기 D-6B adapter는 안전 쪽으로 retry budget을 0으로 두어 POST를 정확히 한 번만 수행한다. `not_found`는 아래의 immutable `created_at_utc` 기반 240초 safety window 안에서만 `failed_known`으로 닫고 새 key를 자동 생성하지 않는다. 향후 같은 key의 두 번째 POST를 활성화하려면 300초 window 안임을 monotonic time으로 증명하고 journal의 attempt 1→2 CAS를 먼저 소비하는 별도 변경과 replay 증거가 필요하다.

### durable journal 상태

submission 상태는 다음으로 제한한다.

```text
staged → submitting → accepted | failed_known | outcome_unknown
staged → cancelled_not_started
outcome_unknown → accepted | failed_known
outcome_unknown(attempt=1) → submitting(attempt=2)  # 같은 key의 예약된 bounded retry 경계
```

delivery 상태는 다음으로 제한한다.

```text
unknown → queued → delivered | failed
unknown → failed
```

- journal에는 operation/idempotency와 Workflow Tool call, exact Capability/snapshot, connection binding, draft hash, 상태·attempt·fixed error·revision·UTC timestamp만 저장한다.
- submission journal에는 수신자, sender, 제목, 본문, endpoint, token, raw request/response와 암호문 payload column을 두지 않는다. 별도 typed Connection profile의 endpoint/fixed sender metadata는 이 항목의 대상이 아니다.
- `created_at_utc`는 staged row insert 시 한 번 기록한 뒤 모든 transition과 startup recovery에서 불변이다. stage insert가 staged→submitting CAS와 POST보다 먼저 durable commit되므로 이 값은 첫 network attempt 시각의 보수적 lower-bound이자 240초 age 계산 기준이다. `updated_at_utc`는 age 계산 기준이 아니지만 transition과 startup recovery가 남긴 더 늦은 durable time lower-bound로 clock rollback을 검출한다.
- insert는 참조한 Workflow run의 Capability ID/version/content hash, catalog revision과 snapshot hash, 그리고 `mail.send.v1 / R3 / Never / Effect` step metadata가 모두 exact match할 때만 허용한다.
- 같은 `toolCallId` create는 immutable identity가 모두 같을 때만 기존 row를 반환한다. 하나라도 다르면 conflict이며 network는 0회다.
- 시작 복구는 `staged → cancelled_not_started`, `submitting → outcome_unknown`만 수행하고 network를 호출하지 않는다.
- 동일 draft/binding의 network-started unresolved `submitting | outcome_unknown`이 있으면 새 submission은 reconciliation 전까지 차단한다.
- staged, submitting, outcome-unknown과 accepted/queued row는 parent cascade를 포함한 삭제를 거부한다. 조정이 끝난 terminal metadata만 일반 보존 정책의 purge 대상이 될 수 있다.

### retry와 reconciliation

- Workflow effect retry는 계속 금지한다.
- POST 결과가 불명확하면 같은 operation ID를 GET으로 정확히 한 번 조회한다. 초기 D-6B adapter는 조회 결과가 authoritative `not_found`여도 adapter 내부에서 두 번째 POST를 수행하지 않으며, 같은 key의 attempt 2 상태는 후속 변경을 위한 예약 경계일 뿐 현재 실행 경로에서는 사용하지 않는다.
- 그 외 timeout, cancellation, transport failure, malformed/oversized response는 `outcome_unknown`으로 남기고 새 operation이나 새 key로 자동 발송하지 않는다.
- 이후 사용자가 동일 draft/binding을 새 AllowOnce로 승인하면 모든 network-started unresolved `submitting | outcome_unknown`을 먼저 조정한다. repository는 `LIMIT 2`로 최대 두 row만 반환하고 한 승인에서는 첫 operation을 GET 최대 한 번만 조회한다. 두 번째 row가 보이면 첫 row가 terminal로 닫혀도 새 POST는 0회이며 다음 명시적 승인을 요구한다.
- D-6C preflight의 schema 19는 `(draft_hash, binding_hash, updated_at_utc DESC, operation_id)` 순서와 `submitting | outcome_unknown` predicate를 가진 partial composite index로 기존 `outcome_unknown` 전용 index를 교체한다. production 조회는 이 index를 `INDEXED BY`로 요구하므로 exact draft/binding prefix에서 시작해 정렬 없이 `LIMIT`에서 멈추며, index가 없거나 schema/query가 drift하면 조회 실패와 POST 0회로 fail closed한다. 실제 production SQL의 `EXPLAIN QUERY PLAN` 회귀는 새 index 사용과 table scan·temporary B-tree 0건을 고정한다.
- Gateway가 선언한 authoritative operation lookup window는 300초다. `ExecuteOnceAsync` 시작에서 주입된 `TimeProvider`로 `executionStartedAtUtc`를 읽지만 exact `not_found`의 240초 나이 판정에 시작시각만 사용하지 않는다. immediate reconciliation과 historical reconciliation 모두 GET 응답을 받은 직후 같은 `TimeProvider`로 UTC를 다시 읽어 `lookupObservedAtUtc`를 만들며, 이 시각에는 shared connection lease 대기와 GET 경과가 반영된다. `created_at_utc`, `updated_at_utc`, `executionStartedAtUtc`, `lookupObservedAtUtc`가 모두 UTC이고 `lookupObservedAtUtc >= executionStartedAtUtc >= max(created_at_utc, updated_at_utc)`이며 `lookupObservedAtUtc - created_at_utc`가 300초에서 60초 safety margin을 뺀 240초 이하일 때만 `not_found`가 `failed_known` terminal 증거다. 어느 clock read든 실패하거나 non-UTC 값, durable lower-bound 위반, 관측 역행 또는 240초 초과가 있으면 authoritative로 취급하지 않고 unresolved를 유지하며 추가·새 POST를 수행하지 않는다. 결정적 회귀는 생성 `t`/실행 `t+230초`/관측 `t+241초`인 window 초과, 생성 `t`/실행 `t+310초`/clock rollback 관측 `t+230초`인 관측 역행, 생성 `t`/recovery 갱신 `t+310초`/실행 `t+100초`/관측 `t+110초`인 durable lower-bound 위반 모두에서 terminal 전이를 거부한다.
- `queued`를 `delivered`로 승격하려면 Gateway의 별도 신뢰 가능한 final evidence가 필요하다.

### 승인 preview와 원문 수명

- 740×500 R3 AllowOnce 창은 sender/connection 표시, 전체 recipient, subject와 plain-text body를 keyboard/screen-reader 접근 가능한 단일 scroll 영역에 보여 준다.
- prompt의 `ToString()`, 결과, exception, activity/audit/workflow/outbox row에는 원문이 나타나지 않는다.
- 승인 거부, validation 실패, binding stale, DB stage 실패는 network 0회다. 승인 후 실행 메모리의 원문은 호출이 끝나면 더 이상 보관하지 않는다.

이 항목은 사용자 입력 자체를 비영속화한다는 뜻이 아니다. 사용자가 채팅에 입력한 문장은 기존 conversation/FTS에 저장되고 모델 context로 전달되며 schema-bound Workflow input은 Sidecar→Desktop IPC를 통과한다. 검토·승인한 Capability 정의의 literal도 Catalog에 저장될 수 있다. 보장은 실행 payload를 submission journal, Workflow ledger, activity/audit, diagnostic과 result에 추가 복제하지 않는 데 적용된다.

## 인수 증거

- `./scripts/verify.ps1` exit 0: PowerShell 5.1 parse, release manifest/evidence, contract generation parity, TypeScript check/build와 .NET warning/error 0을 확인했다.
- 전체 test는 Sidecar 138/138, Contracts 22/22, Application 399/399, Sidecar Integration 1/1, Desktop 750/750이다.
- `./scripts/smoke-ui.ps1`은 IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500, FieldValidation, ManagementSorting, ConversationBoundary, KeyboardNavigation, PrecisionWorkspace와 ApplicationErrors 0으로 통과했다. `InputFocusRestored=not-observed-window-inactive`는 비활성 창에서 관측하지 못한 상태이며 실패가 아니다.
- D-6B에서 release manifest fixture의 current database schema 기대값을 16에서 18로 수정했고, D-6C preflight에서 schema 19의 bounded unresolved lookup index와 다시 일치시켰다.
- 기존 transitive npm low severity advisory 1건은 남아 있지만 verify는 exit 0이며 별도 의존성 호환성 근거가 생길 때까지 추적한다.

## D-6B 완료 시 보장할 결과

- Cline SDK와 Sidecar는 메일 transport, secret, retry나 journal을 소유하지 않는다.
- 고정 Gateway를 기존 generic HTTP와 섞지 않고 endpoint·sender·credential rotation을 Capability version 변경 없이 Connection re-probe/rebind로 복구할 수 있다.
- crash와 timeout 이후 중복 메일을 자동 생성하지 않으며 queued와 delivered 의미를 분리한다.
- D-6B는 offline deferred queue를 제공하지 않는다. network 시작 전 crash 난 staged row는 취소되고 사용자가 새 Workflow와 새 AllowOnce 승인을 해야 한다.

## 대안

- 기존 `connection.http.request.v1`에 POST/body/header를 추가하는 방식은 typed effect와 secret 경계를 깨므로 채택하지 않는다.
- Sidecar/Cline에 SMTP·HTTP client를 넣는 방식은 Desktop 정책과 replaceable runtime 경계를 깨므로 채택하지 않는다.
- SQLite/WAL에 평문 메일을 저장하거나 ticket 폐기 뒤 자동 발송하는 방식은 데이터 수명과 사용자 승인 의미가 불명확하므로 채택하지 않는다.
- draft hash를 idempotency key로 사용하는 방식은 동일 메일의 명시적 재발송을 막고 논리 operation을 구분하지 못하므로 채택하지 않는다.
