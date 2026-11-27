# D-4 Connection Profile과 bound HTTP executor acceptance

> 상태: 인수 완료 · 전체 verify/UI smoke 통과
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0039-desktop-owned-connection-profiles-and-bound-http-executor.md`
> 계약/저장소: Protocol 1.20 · SQLite schema 15

## 현재 판정

D-4의 첫 수직 slice인 Desktop-owned HTTP Connection Profile과 `connection.http.request.v1`은 코드와 결정적 표적 회귀까지 연결됐다. 공유 `.ligcap`의 계약에는 logical connection slot/kind와 선언형 Workflow의 relative routing만 있으며, 중앙 ingress policy가 명백한 endpoint/profile/auth/credential literal을 import·activation·runtime에서 반복 차단한다. 이 PC의 실제 endpoint, profile ID와 Bearer token은 model과 Sidecar 경계 밖에 남는다.

저장소 전체 `./scripts/verify.ps1`과 실제 통합 UI `./scripts/smoke-ui.ps1`도 2026-08-02에 통과했다. 따라서 이 문서는 D-4 internal HTTP 기반 slice의 인수를 완료한다. SQL read-only, mail draft/send와 Git read-only는 아래에 명시한 별도 후속 slice다.

```text
.ligcap Workflow: slot + kind + 고정 relative path template
 → Desktop local profile 선택·Credential Manager secret 저장
 → 실제 probe + exact Capability-version binding
 → activation/rollback readiness 재검사
 → runtime snapshot v2의 opaque connectionBindingHash
 → Cline은 exact Workflow candidate 선택·workflow.invoke만 수행
 → Desktop이 exact Workflow와 binding을 다시 resolve
 → secret 없는 immutable effect 준비·R3 AllowOnce 승인·single-item ticket
 → shared execution lease 아래 metadata 선검사 후 Credential Manager secret fresh read
 → profile identity와 전체 binding/readiness 최종 재검사
 → HTTP/1.1 exact 고정 GET 1회·bounded untrusted result·redacted ledger
```

이번 인수 범위는 internal HTTP GET 한 종류다. SQL read-only, mail draft/send와 Git read-only typed executor는 이 문서가 완료됐다고 주장하는 범위가 아니다.

## 완료 조건별 구현 증거

| 완료 조건 | 구현 및 결정적 증거 |
|---|---|
| schema 14→15와 secret-free local store | `ConversationDatabase.CurrentSchemaVersion`은 15이며 `connection_profiles`, `http_connection_profiles`, `connection_probe_results`, `capability_connection_bindings`를 Desktop SQLite에 추가한다. `ConnectionProfileRepositoryTests.MigratesSchemaFourteenToFifteenWithSecretFreeConnectionTables`가 migration, FK integrity와 민감 column 0개를 확인한다. `ExactDraftBindingRoundTripsAndProfileChangesInvalidateOnlyTheRequiredEvidence`는 DB/WAL/SHM에서 token canary 0건과 재시작 round trip을 확인한다. |
| Credential Manager와 profile lifecycle | `WindowsConnectionCredentialStore`만 Bearer token 원문을 읽고 바꾸고 삭제한다. readiness의 `ExistsAsync`는 `WindowsCredentialStore.Exists` 전용 경로로 `CredRead` blob pointer/size만 확인하고 `CredFree`하여 managed secret string을 만들지 않는다. write/probe/execution은 8~1,280자 token68 policy를 공유하고 native write buffer는 `ZeroFreeCoTaskMemUnicode`로 지운다. `ConnectionManagementService`는 `staged → updating → stable`을 거치며 credential write/delete 실패를 `error`와 고정 오류로 남긴다. 불안정 profile의 `Keep`, 새 Bearer 진입의 `Keep`과 `None` 전환 중 잔존 credential을 허용하지 않고 명시적 replace/delete 재시도만 stable로 복구한다. credential mutation은 같은 execution lease로 직렬화된다. create/failure/recovery/orphan/concurrent mutation 회귀가 이 경계를 고정한다. |
| 실제 probe와 revision-bound receipt | `ConnectionHttpProbe`는 shared lease 전체를 잡고 저장된 base endpoint와 fresh credential 한 세대에 HTTP/1.1 exact, `Connection: close`, non-null 0-byte content의 고정 GET 한 번만 보낸다. response body는 읽거나 저장하지 않고 redirect, missing credential, timeout/transport를 고정 오류로 축약한다. 실제 `TcpListener`가 header 직후 연결을 닫는 회귀에서 wire request가 1회임을 확인하며, old endpoint/token probe 중 mutation은 receipt 기록까지 기다린다. current target/credential revision이 바뀐 in-flight receipt도 저장하지 않는다. |
| exact-version local binding과 opaque generation | binding PK와 FK는 capability ID/version/content hash/slot의 immutable draft identity에 결합되고 repository CAS를 사용한다. `ConnectionManagementService.CreateOpaqueBindingHash`는 새 binding generation마다 32-byte CSPRNG 입력의 SHA-256 digest를 만든다. 동일한 binding identity를 다시 저장하면 기존 generation을 보존하고 profile/target/credential identity가 달라진 실제 rebind만 revision과 opaque digest를 새로 만든다. `BindingCapturesExactDraftAndProfileIdentityAndRenameIsANoOp`, `BindingGenerationIsOpaqueAcrossIndependentDesktopStores`, `BindingToAnIdentityThatIsNotAnImmutableDraftFailsWithoutWritingARow`가 이를 확인한다. |
| readiness와 activation/rollback fail-closed | `ConnectionReadinessResolver`는 exact version, ordinal slot/kind, stable/enabled profile, captured target/credential revision, current successful probe와 Bearer secret 존재를 함께 검사한다. secret 값은 읽지 않고 `WindowsCredentialStore.Exists` 경로로 존재 여부만 확인한다. `ConnectionReadinessResolverTests`의 ready/rename/shared-profile/stale/limit 회귀와 `DisableAndReenableInvalidateProbeAndRequireAReboundExactVersion`, `CapabilityCatalogServiceTests.ActivationAndRollbackAllowOnlyExactVersionReadyConnections`가 disable/re-enable 각각의 exact evidence 무효화와 not-ready일 때 repository mutation 0회, 새 probe+rebind를 거친 ready exact target에서만 activation/rollback을 확인한다. |
| runtime snapshot v2와 frozen authorization 결속 | `CapabilityRuntimeSnapshotBuilder`는 exact version별 readiness를 resolve하고 ready 후보만 투영하며 aggregate를 candidate의 `connectionBindingHash`와 snapshot hash에 포함한다. binding generation 변경은 같은 exact Capability라도 새 snapshot을 만든다. `ResolvesConnectionsForEachExactVersionAndOmitsTheNotReadyCandidate`, `ChangedConnectionBindingChangesCandidateAndSnapshotHashes`와 `CapabilityRunAuthorizationPolicyTests`의 stale binding hash selection/context 거부가 이 결속을 고정한다. |
| Workflow-only HTTP와 canonical metadata | `WorkflowStaticValidator`는 `connection.http.request.v1`이 `requiredTools`와 exact declared HTTP slot을 모두 사용하도록 강제하고, connection slot/method/path template과 parameter 이름을 package-defined literal로 고정한다. `DesktopWorkflowToolMetadataCatalog`는 이 Tool을 `R3 / OneShot / Never / Effect`로 등록한다. `ValidatesBoundHttpGetWithExactDeclaredConnectionAndR3OneShotMetadata`, `RejectsModelControlledHttpShapeAndRetry`, `RejectsUndeclaredAndUnusedHttpConnectionSlots`가 static admission을 확인한다. 일반 `ExecuteAsync`와 Capability context 없는 호출은 `DirectInvocationAndMissingCapabilityContextFailClosedWithoutSending`에서 network send 0회로 거부된다. |
| preparation·송신·mutation의 한 경계 | `DesktopConnectionHttpExecutionResolver`는 preparation 때 secret을 materialize하지 않고 exact Workflow/version/slot/readiness/aggregate/profile identity를 확인한다. execution 때 Desktop-wide lease를 얻은 뒤에만 Credential Manager에서 token을 fresh read하고 profile과 전체 readiness를 마지막으로 다시 읽으며, 성공 시 lease를 HTTP adapter에 이전해 response 처리가 끝날 때까지 보유한다. Save/Bind/probe도 같은 lease를 사용한다. stale 거부 회귀와 `ExecutionLeaseStaysHeldUntilTheHttpAttemptCompletes`, `ProfileMutationWaitsForTheSharedExecutionLease`, 실제 probe race가 final resolve→send/receipt 사이 target·credential 혼선을 막는다. |
| bounded HTTP transport와 fixed disposition | `ConnectionHttpRequestTool`은 canonical base 아래 relative target만 만들고 redirect/cookie/proxy/default credential/caller header·body를 허용하지 않는다. 실제 요청은 HTTP/1.1 exact, `Connection: close`, adapter-owned non-null 0-byte content다. header 송신 뒤 premature EOF를 만든 실제 socket 회귀가 wire request 1회임을 확인한다. declared/unknown-length body exact N/N+1, invalid UTF-8, 16,000 Unicode scalar와 send 후 timeout/transport의 `Unknown` disposition을 검증한다. 성공으로 내보내기 전에 content type과 full decoded text에 현재 credential의 raw 또는 한 번의 JSON escape 반사가 있으면 `connection.http.credential_reflected`, 빈 output으로 확정 실패한다. |
| package local-detail ingress guard | `CapabilityIngressPolicy`는 manifest/instructions/workflow/input/settings/tests의 canonical package payload를 중앙에서 검사하고 absolute HTTP(S), endpoint/baseUrl/profileId/auth/credential target 문맥과 명백한 host/port/scheme 조합을 `package.connection_literal.detected` 또는 `capability.connection_literal.detected`로 거부한다. catalog submit/activate/rollback, ZIP codec, persisted/runtime snapshot과 Workflow orchestration이 같은 guard를 거친다. `$schema` URI, logical slot/kind와 relative template은 허용한다. 이 lint는 임의 alias·인코딩·은어를 해독하는 증명이 아니므로 package author/import review는 별도 신뢰 경계다. Desktop은 실제 local 값을 package에 주입하지 않는다. |
| D-3 one-shot ledger 재사용 | immutable prepared effect의 payload/binding hash, expiry와 execution-time re-resolution이 D-3 single-item ticket 및 step reservation 뒤에 실행된다. known remote 실패는 raw upstream body 없이 fixed error와 `failed_known`으로 영속화된다. 실제 handler transport 예외는 send 1회, step `outcome_unknown/unknown`, run `attention_required/review_outcome`으로 남고 raw detail은 없다. 승인 callback 안의 cancellation은 send 0회, step `not_started`, run `cancelled/retry_from_start`로 남는다. |
| Connections와 Capability review UI | `ConnectionsPage`는 local HTTP profile, enabled/auth, write-only `PasswordBox`, token keep/replace/delete, 실제 연결 확인과 readiness 상태를 제공한다. `CapabilityReviewWindow`는 logical slot마다 local profile을 선택·저장하고 readiness가 없으면 activate/rollback을 비활성화하며, 사용자 확인 전후로 binding fingerprint를 다시 검사한다. XAML/construction 회귀와 실제 UIA smoke가 이 경계를 확인했다. Smoke는 Connections를 포함한 IntegratedPages 7, TopLevelWindows 1, 760×500 compact layout, focus/validation/sorting/conversation boundary/keyboard/Precision Workspace 통과와 ApplicationErrors 0이다. |
| 실제 SQLite CAS와 Named Pipe detail 0건 | 서로 다른 `ConversationStore`/repository가 같은 snapshot으로 profile 또는 binding을 동시에 CAS할 때 정확히 하나만 성공하고 persisted row가 winner와 일치한다. 실제 bundled Sidecar/current-user Named Pipe E2E에서는 package requirement가 slot/kind 두 field뿐이며 wire candidate, selector, `CapabilityInvocationContext`, `workflow.invoke`에는 opaque hash만 존재한다. endpoint/profileId/auth/token/secret property와 local sentinel은 wire JSON에 0건이다. |

## 계약과 프로세스 경계

- Protocol 1.20의 생성 계약 hash는 `0ea99696c9321ca287589508fb54d20f2ab06166ddbdd4a232619deda88b196a`다. 최종 verify의 contract generation check가 C#/TypeScript 생성물과 이 값을 다시 확인했다.
- `contracts/tools/connection.http.request.v1.schema.json`은 `connectionSlot`, 고정 `GET`, `pathTemplate`, bounded `reason`, 최대 16개 named path parameter와 32개 named query parameter만 허용한다. absolute URL, host, port, profile ID, auth, header, body와 secret field는 없다.
- result 계약은 2xx `statusCode`, bounded `contentType`, text, `truncated`, 최대 49,152의 `responseBytes`, 상수 `untrusted=true`만 허용한다. `GeneratedCapabilityContractTests.ConnectionHttpContractExposesNoRawTransportOrSecretFields`가 C#/TypeScript registry의 canonical pair와 field allowlist를 확인한다.
- runtime snapshot schema v2는 candidate마다 opaque `connectionBindingHash` 하나만 Sidecar에 전달한다. persisted local binding generation, profile ID, endpoint, auth kind, target/credential revision과 probe receipt는 IPC 대상이 아니다.
- Sidecar는 Connection repository, Credential Manager와 HTTP socket을 소유하지 않는다. Workflow candidate 선택 뒤 exact `workflow.invoke.v1`을 보내고 결과를 모델 turn에 재투입할 뿐이다. Workflow definition, path template, ticket, nonce와 prepared effect도 IPC에 포함하지 않는다.

## 저장, credential과 readiness

SQLite schema 15의 네 table은 다음 local metadata만 저장한다.

- `connection_profiles`: kind, display/lifecycle/enabled, optimistic revision, target/credential revision과 target hash
- `http_connection_profiles`: canonical base URL과 `none`/`bearer` auth kind
- `connection_probe_results`: exact target/credential revision, target hash, 성공 여부, fixed error와 확인 시각
- `capability_connection_bindings`: exact Capability ID/version/content hash와 slot/kind, local profile ID, captured revision/hash, binding revision과 opaque `binding_hash`

Bearer token 원문은 Windows Credential Manager에만 있으며 SQLite, package, runtime snapshot, prepared effect, ticket와 audit metadata에 복사하지 않는다. Readiness는 `CredRead` blob의 존재와 size만 보는 `WindowsCredentialStore.Exists`를 사용해 secret managed string을 만들지 않는다. Replace/profile-save/probe/execution은 8~1,280자의 shared token68 grammar를 적용하고, native write buffer는 `ZeroFreeCoTaskMemUnicode`로 지운다. Profile 변경은 Credential Manager와 SQLite를 하나의 transaction으로 묶을 수 없으므로 변경 중 상태를 ready로 간주하지 않는다. 실패한 replace/delete는 `Keep`으로 우회해 stable이 될 수 없고 명시적 재시도로만 회복한다.

`binding_hash`와 runtime `connectionBindingHash`는 서로 다른 층이다.

1. Desktop은 새 local binding generation마다 CSPRNG 32 bytes를 만들고 그 digest를 `binding_hash`로 저장한다. 이 값은 endpoint/profile/revision에서 결정적으로 재계산하지 않는다.
2. exact하게 같은 binding을 다시 저장하는 idempotent 호출만 기존 `binding_hash`를 보존한다. 다른 profile 또는 target/credential identity로 rebind하면 새 revision과 새 opaque digest를 만든다.
3. `ConnectionReadinessHasher`는 exact Capability ref와 ordinal resolved evidence, 이 opaque generation을 length-prefixed deterministic aggregate로 만든다. empty requirements도 exact-version-bound deterministic aggregate를 가진다.
4. Sidecar와 model은 aggregate `connectionBindingHash`만 보며 local identity를 복원할 수 없다. binding 또는 readiness evidence가 바뀌면 candidate와 snapshot hash가 달라지고 과거 run context는 stale이다.

Display name만 바뀌면 endpoint/auth/credential identity와 성공 probe를 보존한다. Endpoint/auth/token/lifecycle 변경은 target/credential revision 또는 readiness를 무효화한다. 인증 방식만 `None → Bearer`로 바뀌어도 probe, readiness와 binding identity를 모두 무효화한다. Enabled를 끄거나 다시 켜는 각 변경은 `targetRevision`을 증가시켜 기존 probe와 exact binding을 stale로 만든다. 변경된 profile을 다시 probe하고 exact binding을 다시 저장하기 전에는 activation/runtime admission이 닫힌다.

## Workflow, 승인과 at-most-once

HTTP Tool은 package의 선언형 Workflow에서만 사용할 수 있다.

- `requiredConnections`는 logical slot/kind만 선언하고 exact Workflow step의 `connectionSlot`이 이를 literal로 참조한다.
- method는 `GET`, path template과 parameter 이름은 exact package definition에 고정된다. parameter 값만 bounded Workflow input/이전 step binding으로 채울 수 있다.
- runtime은 path/query 값을 각각 percent-encode하고, empty/`.`/`..` path value, absolute/leading-slash template, backslash와 URL control delimiter를 거부한다. 최종 absolute URI가 canonical base 문자열과 exact하게 결합되고 같은 base prefix 안에 있을 때만 준비된다.
- preparation은 local display name, canonical base identity, frozen aggregate와 execution fingerprint만 복사하며 credential을 읽거나 보유하지 않는다.
- approval에는 local connection display, relative target, package-fixed template과 bounded reason을 보여준다. `AllowOnce` 외 persistent grant와 retry는 없다.
- ticket/step reservation 뒤 execution resolve가 Desktop-wide execution lease를 얻고 exact context, aggregate와 예상 profile metadata를 선검사한다. 그 다음 Bearer token을 fresh read하고 profile identity와 전체 readiness aggregate를 최종 재검사한다. 이 마지막 검사까지 통과한 token만 한 요청의 `Authorization` header에 사용되며 lease는 bounded response 처리가 끝날 때까지 유지된다. Profile save/bind와 실제 probe도 같은 lease를 사용한다.
- prepared effect는 expiry와 process-local one-shot state를 가지며 expired/already-consumed/cancel-before-send는 network send 0회다.

## HTTP transport와 결과 경계

- canonical `http`/`https` base endpoint + validated relative target + HTTP/1.1 exact GET 한 번
- automatic redirect, cookie, proxy/environment proxy, default/Windows credential와 pre-auth 비활성
- caller-supplied body, upload와 arbitrary header 없음; adapter-owned `Accept`, `User-Agent`, 선택된 Bearer `Authorization`, `Connection: close`와 non-null 0-byte content만 설정
- 요청 header/effect가 시작된 뒤 premature EOF 재전송 없음. 실제 loopback socket 회귀가 request/probe 각각 wire request 1회임을 확인한다. 요청 byte를 보내기 전의 안전한 connection-establishment 재시도까지 금지한다고 주장하지 않는다.
- send timeout 30초, Tool host timeout 35초
- response header 16 KiB 상한
- decompressed response body 48 KiB exact 상한: declared `Content-Length` N+1은 stream read 전에 거부하고 exact N은 허용하며, 길이를 모르는 stream도 N+1 byte에서 거부
- 허용 content type은 UTF-8 `application/json`, `text/plain`, `+json`; handler 뒤 residual content encoding과 invalid UTF-8 거부
- text는 최대 16,000 Unicode scalar로 잘라 `truncated=true`를 표시하되 byte 초과 body는 성공으로 truncate하지 않음
- result는 항상 `untrusted=true`; response body, redirect location와 raw exception은 fixed error에 포함하지 않음
- 현재 Bearer credential의 exact raw 값 또는 한 번 JSON-escape된 값이 content type/full decoded text에 반사되면 output을 만들기 전에 빈 결과의 `connection.http.credential_reflected`로 폐기

실제 adapter fixed code와 disposition은 다음과 같다.

- send 전 `NotStarted`: `connection.http.context_required`, `input_invalid`, `binding_unavailable`, `binding_mismatch`, `credential_unavailable`, `binding_invalid`, `expired`, `already_consumed`, `cancelled`
- response를 받은 뒤 확정된 `FailedKnown`: `connection.http.redirect_denied`, `connection.http.remote_failed`, `connection.http.content_unsupported`, `connection.http.response_too_large`, `connection.http.credential_reflected`
- `SendAsync`가 시작된 뒤 결과를 확정할 수 없는 `Unknown`: `connection.http.timeout`, `transport_failed`, 실행 중 `cancelled`

`Unknown`은 자동 retry하지 않고 D-3 Workflow를 `attention_required/review_outcome`으로 승격한다. fixed code와 redacted summary만 ledger/audit 경계로 전달한다. Credential reflection 방어는 실수로 되돌아오는 raw/JSON 표현을 차단하는 경계이며, configured endpoint가 임의 encoding·분할을 사용하는 covert channel까지 판별하는 일반 해법은 아니다. 따라서 package author와 사용자가 구성한 endpoint는 검토된 사내 신뢰 경계로 남는다.

## 검증 결과

표적 회귀는 다음과 같다.

- Application Capability policy/catalog/runtime snapshot filter: 76/76 통과
- Desktop Connection HTTP/profile/repository/workflow/ZIP/runtime 8개 class filter: 119/119 통과
- Desktop 실제 SQLite profile/binding CAS와 auth-only invalidation 추가 filter: 2/2 통과
- Desktop Workflow HTTP ledger class: 3/3 통과
- Desktop probe replay class: 8/8 통과
- 실제 Sidecar/Named Pipe `D2CapabilityProjectionE2ETests`: 2/2 통과

최종 gate도 2026-08-02에 닫혔다.

- `./scripts/verify.ps1`: PowerShell 5.1 parse, release manifest/evidence, locked sidecar install, contract generation check, TypeScript check/build, 전체 test, npm audit, .NET format/build/test 통과
- Sidecar: 123/123
- Contracts: 18/18
- Application: 187/187
- Sidecar Integration: 1/1
- Desktop: 596/596
- .NET build: warning 0, error 0
- `./scripts/smoke-ui.ps1`: IntegratedPages 7, TopLevelWindows 1, CompactLayout 760×500, 모든 interaction 항목 통과, ApplicationErrors 0

`npm audit`의 기존 `dify-ai-provider → @ai-sdk/provider-utils` low advisory 1건은 verify exit 0으로 계속 추적한다. 호환성이 검증되지 않은 강제 major override는 적용하지 않았다.

## 남은 범위와 알려진 비차단 항목

- SQL read-only, mail draft/send와 Git read-only는 별도 typed contract/adapter/risk 결정을 가진 후속 slice다. generic HTTP나 shell로 대신하지 않는다.
- Package ingress lint는 구조적 금지와 명백한 literal을 방어하지만 임의 alias, 자연어 은어, 다중 인코딩을 해독하는 증명은 아니다. import source/package author review를 신뢰 경계로 유지하고 Desktop은 local profile 값을 package에 주입하지 않는다.
- Credential reflection guard는 exact raw/한 번 JSON-escaped 반사를 막는다. percent/base64/분할 같은 임의 재인코딩은 일반 response 기능과 양립하는 완전 차단이 불가능하므로 configured endpoint 신뢰와 운영 검토 대상으로 남는다.
- HTTP handler 자체가 16 KiB를 넘는 response header를 `SendAsync` 안에서 먼저 중단하면 adapter가 이를 `response_too_large/FailedKnown` 대신 `transport_failed/Unknown`으로 축약할 수 있다. 어느 경우든 body와 credential은 노출하지 않고 fail-closed하지만, native handler별 fixed-code 정밀도는 후속 transport integration 보강 대상이다.
- 실제 사내 endpoint의 인증서·proxy·네트워크 정책 호환성은 배포 환경의 운영 acceptance 대상이다. 제품 기본은 system/environment proxy를 사용하지 않는다.
- `http` profile도 폐쇄망 호환을 위해 허용하지만 Bearer를 평문으로 전달할 수 있다. 운영 정책은 가능한 경우 `https`를 사용하고 필요하면 후속 HTTPS-only Bearer 정책을 적용해야 한다.
- Windows 10/11과 실제 사내 인증서·proxy 조합의 장치별 실기는 자동 UIA/loopback 검증을 대체하지 않는 배포 환경 acceptance다.
