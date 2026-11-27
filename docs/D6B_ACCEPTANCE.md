# D-6B fixed Mail Gateway acceptance

> 상태: 인수 완료
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0042-fixed-mail-gateway-and-durable-submission-journal.md`
> 현재 계약: Protocol 1.23 · contract hash `4230d92e94aa21e6dd9f166ed8ed7c30b8a9827c3ebd93ef8f787b848297ace1`

## 현재 판정

D-6B의 계약·정책·저장소·Connection·Gateway·prepared effect·UI 구성 요소와 결정적 wire 수직·replay·raw canary 회귀를 구현했다. 저장소 전체 `./scripts/verify.ps1`과 `./scripts/smoke-ui.ps1`까지 통과해 fixed Mail Gateway slice를 인수 완료한다.

## 구현 단계

- [x] B1: Protocol 1.23 `mail.send.v1` exact 계약과 generated C#/TypeScript parity
- [x] B1: `MailSendPolicy`, dominating draft hash binding과 `mail-api` admission
- [x] B1: SQLite schema 17 metadata-only submission journal, CAS와 startup recovery
- [x] B2: SQLite schema 18 typed Mail Gateway profile/probe/binding, 별도 Credential Manager Bearer token, probe와 composite readiness
- [x] B2: fixed probe/submit/reconciliation wire, HTTP/Mail execution resolver와 production composition의 동일 `ConnectionExecutionLeaseCoordinator` cross-kind gate
- [x] B2: production Workflow-only `mail.send.v1` R3 AllowOnce prepared one-shot Tool, durable CAS와 queued/delivered 구분
- [x] B2: typed Connections/Capability review UI와 740×500 스크롤 가능한 full-mail approval preview
- [x] 결정적 MailSend + 실제 Workflow 수직 + outbox/replay/raw canary 선택 회귀
- [x] 저장소 전체 verify와 UI smoke

## 인수 증거

- Contracts D-6B selected regression: 15/15
- Application D-6B selected regression: 92/92
- Desktop `MailSendTool` + vertical + outbox selected regression: 30/30
- broader Desktop security/wiring selected regression: 121/121
- Desktop targeted build: warning 0, error 0
- `./scripts/verify.ps1`: exit 0. PowerShell 5.1 parse, release manifest/evidence, contract generation parity, TypeScript check/build 통과
- 전체 test: Sidecar 138/138, Contracts 22/22, Application 399/399, Sidecar Integration 1/1, Desktop 750/750
- 전체 .NET build: warning 0, error 0
- `./scripts/smoke-ui.ps1`: IntegratedPages 8, TopLevelWindows 1, CompactLayout 760×500, FieldValidation, ManagementSorting, ConversationBoundary, KeyboardNavigation, PrecisionWorkspace 통과, ApplicationErrors 0
- UI smoke의 `InputFocusRestored=not-observed-window-inactive`는 대상 창이 비활성이라 포커스 복원을 관측하지 않은 상태이며 실패가 아니다.
- release manifest fixture의 current database schema 기대값을 16에서 18로 바로잡아 schema 17/18 migration과 release evidence를 일치시켰다.
- `npm audit`의 기존 transitive low severity advisory 1건은 남아 있지만 전체 verify는 exit 0이며, 호환성 근거 없이 lockfile을 강제 변경하지 않는다.

위 표적·전체 증거를 D-6B 완료 기준으로 고정한다.

계약 schema는 operation/hash의 길이를 제한하고 producer/repository가 exact lowercase 형식을 강제한다.

- public operation ID: `mgo_` + lowercase hex 64자
- Desktop-only idempotency key: `mik_` + lowercase hex 64자
- draft/binding/snapshot/content hash: `sha256:` + lowercase hex 64자
- operation/idempotency key는 Workflow/model 입력에 존재하지 않는다.

고정 wire acceptance는 다음 route 외의 mapping을 허용하지 않는다.

- probe: `GET /.well-known/ligclaw-mail-gateway/v1`
- submit: `POST /ligclaw-mail-gateway/v1/submissions`
- reconcile: `GET /ligclaw-mail-gateway/v1/submissions/{operationId}`

성공 result의 `queued`는 Gateway 접수, `delivered`는 신뢰 가능한 final evidence만 뜻한다. known failure와 outcome unknown은 성공 status가 아니라 fixed error와 journal state로 남는다.

초기 adapter의 retry budget은 0이다. 한 승인 실행은 POST를 최대 한 번 수행하고, 그 결과가 불명확할 때 같은 operation을 즉시 GET으로 최대 한 번 조정하며 두 번째 POST를 수행하지 않는다. 이후 새 AllowOnce 승인에서는 같은 draft/binding의 모든 network-started unresolved 상태인 `submitting | outcome_unknown`을 대상으로 조회한다. repository query는 `LIMIT 2`로 반환 row를 최대 두 건으로 제한하고 한 승인에서 첫 operation 하나만 GET한다. 두 번째 row가 보이면 첫 row를 terminal로 닫더라도 새 POST 없이 다음 명시적 승인을 요구한다.

`LIMIT 2`는 반환 row와 승인당 network GET을 제한하지만 SQLite의 scan/sort 작업까지 엄격히 bounded하다는 뜻은 아니다. 현재 partial composite index는 `outcome_unknown`만 대상으로 하므로 `submitting | outcome_unknown` 전체를 조회하는 극단적으로 큰 journal에서는 추가 scan/sort 최적화가 필요할 수 있다. 이는 안전성 blocker가 아닌 nonblocking medium 성능 후속이며 D-6B follow-up 또는 D-6C preflight에서 측정·보강한다. 잔여 row가 있으면 새 POST가 0회인 fail-closed 경계는 그대로다.

`created_at_utc`는 stage insert 때 기록되고 이후 바뀌지 않으며, staged→submitting CAS와 POST보다 먼저 durable commit되는 첫 network attempt의 보수적 lower-bound다. `updated_at_utc`는 240초 나이 계산의 기준이 아니지만 transition/startup recovery가 남긴 더 늦은 durable time lower-bound로 clock rollback을 검출한다. Gateway의 300초 authoritative lookup window에서 60초 safety margin을 뺀 240초 이내에서만 exact `not_found`를 terminal `failed_known`으로 확정한다. `ExecuteOnceAsync` 시작 때 주입된 `TimeProvider`로 `executionStartedAtUtc`를 읽되, 240초 나이 판정에 시작시각만 재사용하지 않는다. 즉시 reconciliation과 historical reconciliation 모두 GET 응답을 받은 직후 같은 `TimeProvider`로 `lookupObservedAtUtc`를 다시 읽으므로 shared lease 대기와 실제 GET 경과시간까지 포함한다. 네 시각이 모두 UTC이고 `lookupObservedAtUtc >= executionStartedAtUtc >= max(created_at_utc, updated_at_utc)`이며 `lookupObservedAtUtc - created_at_utc <= 240초`일 때만 terminal이다. 어느 clock read든 실패하거나, non-UTC 값·durable lower-bound 이전 실행·관측시각 역행·240초 초과가 있으면 unresolved를 유지하고 추가·새 POST 0회로 fail closed한다.

## 반드시 증명할 경계

- send hash는 앞선 draft step의 exact hash이며 runtime 재계산과 일치한다.
- sender, endpoint, token, operation/idempotency key는 Workflow/Sidecar/model 입력에 없다.
- DB stage나 CAS가 실패하면 POST는 0회다.
- same tool-call exact duplicate는 같은 operation이고 identity mismatch는 send 0회다.
- staged/submitting crash recovery는 network 없이 cancelled/outcome-unknown으로 수렴한다.
- 초기 실행의 POST는 최대 1회이며 ambiguous 결과의 즉시 reconciliation GET도 최대 1회다.
- 새 승인 조회 대상은 `submitting | outcome_unknown` 전체이며 `LIMIT 2`로 반환 row를 최대 두 건, 첫 row GET을 최대 1회로 제한한다. 두 번째 row가 있으면 새 POST는 0회다. 이 제한은 DB scan/sort 작업 자체가 strictly bounded라는 주장이 아니다.
- immutable `created_at_utc`가 CAS/POST 전 stage 시각 lower-bound이고 `updated_at_utc`는 age 기준이 아닌 더 늦은 durable lower-bound다. GET 응답 직후 재샘플한 관측시각에 대해 `lookupObservedAtUtc >= executionStartedAtUtc >= max(created_at_utc, updated_at_utc)`와 관측–생성 240초 이하를 모두 만족해야 `not_found`를 terminal로 확정한다. 네 시각은 모두 UTC여야 하고 clock read 실패/non-UTC/durable lower-bound 위반/관측 역행/240초 초과에서는 unresolved와 추가·새 POST 0회를 유지한다.
- 시간 회귀는 생성 `t`/실행 `t+230초`/관측 `t+241초`인 window 초과, 생성 `t`/실행 `t+310초`/clock rollback 관측 `t+230초`인 관측 역행, 그리고 생성 `t`/recovery 갱신 `t+310초`/실행 `t+100초`/관측 `t+110초`인 durable lower-bound 위반 모두에서 `not_found` terminal 전이를 거부한다.
- queued는 delivered로 표시되지 않고 unknown 뒤 새 key 자동 retry가 없다.
- profile target/credential 변경은 probe·binding·snapshot을 stale로 만든다.
- production composition의 HTTP/Mail profile·credential·probe·binding mutation과 두 kind의 execution final resolution은 같은 `ConnectionExecutionLeaseCoordinator`를 공유해 mixed aggregate TOCTOU를 차단한다.
- fixed route/header/body 밖의 redirect/proxy/cookie/default credential/generic POST가 없다.
- recipient/sender/subject/body/token sentinel은 결과·오류·audit·workflow ledger·outbox에 남지 않는다.
- 이 비복제 보장은 기존 conversation/model/IPC와 Capability Catalog의 입력 원본까지 비영속이라고 뜻하지 않는다. 채팅 입력은 conversation/FTS와 모델 context에, schema-bound Workflow input은 Sidecar→Desktop IPC에, 검토·승인한 literal 정의는 Catalog에 존재할 수 있다.
- 8,192자 본문을 포함한 전체 승인 preview가 740×500에서 스크롤 가능하고 keyboard/screen-reader 경계를 깨지 않는다.

## 의도적으로 미지원

- attachment와 SMTP는 D-6C다.
- HTML, arbitrary header, reply-to와 generic mail API mapping은 지원하지 않는다.
- 평문 또는 암호화 payload를 보관해 재시작 뒤 자동 발송하는 offline queue는 별도 보안 결정 전까지 지원하지 않는다.
