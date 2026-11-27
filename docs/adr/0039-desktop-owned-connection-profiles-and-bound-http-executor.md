# ADR 0039: Desktop-owned Connection Profile과 bound HTTP executor

- 상태: 승인 · D-4 internal HTTP 기반 slice 인수 완료
- 날짜: 2026-08-02

## 배경

D-3는 exact Capability version을 frozen run에 고정하고 Workflow 정의, 승인, one-shot ticket, Tool 실행과 durable ledger를 Desktop 소유 경계에 두었다. D-4에서는 공유 `.ligcap`이 설치된 PC의 사내 HTTP 연결을 사용하게 해야 한다.

Capability package에 endpoint나 token을 넣거나 Cline Sidecar가 HTTP를 직접 실행하게 하면 package 공유, 모델 prompt, IPC와 진단 로그에 로컬 연결 정보가 섞인다. 반대로 Capability version과 로컬 Connection binding을 느슨하게 결합하면 profile이 바뀐 뒤 이미 승인한 run이 다른 endpoint나 credential로 조용히 전환될 수 있다.

.NET의 기본 HTTP 동작도 그대로 사용하지 않는다. 자동 redirect, cookie, Windows 기본 credential과 환경 proxy는 검토한 target 밖으로 요청이나 credential을 보낼 수 있고, 요청 header를 보낸 뒤 premature EOF 재전송은 결과가 불명확한 R3 effect를 반복할 수 있다.

## 결정

### 프로세스와 소유권

- Cline SDK를 사용하는 bundled Node.js Sidecar를 유지한다. Sidecar는 후보 선택, bounded Workflow invoke와 결과 재투입만 소유한다.
- Connection profile metadata, exact Capability-version binding, probe receipt와 readiness는 Desktop이 소유하고 SQLite schema 15에 저장한다.
- HTTP endpoint, auth mode, credential revision과 token은 Sidecar, model, Capability package와 로그로 보내지 않는다.
- token과 password 원문은 Windows Credential Manager에만 저장한다. SQLite에는 secret 원문, reversible 암호문과 secret digest를 저장하지 않는다.
- 실제 HTTP socket, target 검증, secret 조회·주입, 승인, one-shot 실행과 audit은 Desktop adapter가 소유한다.
- Profile save/bind, actual probe와 HTTP execution은 하나의 Desktop-wide execution lease를 사용해 final resolve부터 response/receipt 처리까지 target·credential 세대를 직렬화한다.
- Sidecar의 임의 HTTP client, model-facing generic HTTP Tool, PowerShell/cmd/curl과 arbitrary shell 우회는 D-4 실행 경로가 아니다.

```text
.ligcap: logical slot + kind only
  → Desktop exact-version local binding
  → secret-free runtime snapshot v2 connectionBindingHash
  → Workflow selects exact frozen Capability
  → Desktop re-resolves and revalidates binding/readiness
  → immutable HTTP prepared effect
  → Desktop AllowOnce approval + one-shot ticket
  → Desktop execution lease 안에서 Credential Manager secret을 주입하고 한 번 송신
```

### SQLite schema 15와 Credential Manager

Schema 15는 다음 physical table을 Desktop repository 뒤에 둔다.

- `connection_profiles`: local profile ID, kind, lifecycle, display metadata, target/credential revision, target hash, updated time와 optimistic revision
- `http_connection_profiles`: canonical base endpoint와 `none`/`bearer` auth kind
- `capability_connection_bindings`: exact capability ID/version/content hash, ordinal logical slot/kind, local profile ID, captured target/credential revision과 opaque binding generation
- `connection_probe_results`: profile ID, exact target/credential revision, probe status, fixed error와 probe time

Profile ID와 Credential Manager target은 로컬 식별자다. export/import package, runtime snapshot과 model Tool argument에 포함하지 않는다. Credential Manager write와 SQLite update는 하나의 transaction이 될 수 없으므로 변경 중 profile을 ready로 보지 않는다. 변경이 완전히 끝나고 새 probe가 성공하기 전까지 lifecycle/readiness는 fail-closed다. Readiness의 secret 존재 확인은 `WindowsCredentialStore.Exists`가 `CredRead` blob pointer/size만 확인하고 `CredFree`하는 경로를 사용해 managed secret string을 만들지 않는다. Credential replace와 profile-save/probe/execution은 8~1,280자의 공유 token68 grammar를 적용하며, write의 native buffer는 `ZeroFreeCoTaskMemUnicode`로 해제한다. Failed replace/delete 상태의 `Keep`, 새 Bearer 진입의 `Keep`과 `None` 전환 중 잔존 credential은 허용하지 않는다. 명시적 replace/delete 재시도만 profile을 stable로 복구한다.

Profile lifecycle은 enabled와 stable 상태를 명시적으로 구분한다. readiness는 다음이 모두 참일 때만 성립한다.

- profile이 enabled이고 stable함
- binding의 kind가 profile의 canonical `http` kind와 일치함
- binding이 캡처한 target/credential revision과 target hash가 현재 profile과 일치하고 opaque binding generation 형식이 유효함
- 현재 revision 조합에 대한 성공 probe receipt가 있음
- bearer auth profile이면 현재 Credential Manager secret이 존재함

Probe receipt에는 response body, endpoint, auth header, token이나 raw exception을 저장하지 않는다. 오래되거나 다른 revision의 성공 probe는 readiness 근거로 재사용하지 않는다.

### Package 선언과 exact local binding

공유 package는 `requiredConnections`에 logical slot과 kind만 선언한다.

```json
{
  "requiredConnections": [
    { "slot": "service", "kind": "http" }
  ]
}
```

Package 계약에는 다음 값을 넣을 수 없다.

- local profile ID나 credential target
- scheme, host, port와 absolute URL
- auth mode, token, password와 임의 header
- proxy, redirect, cookie와 Windows credential 설정

이 금지는 package-owned relative routing까지 없앤다는 뜻이 아니다. Exact Workflow definition은 base endpoint를 포함하지 않는 relative `pathTemplate`, named path/query parameter 이름과 그 값의 선언형 binding을 가질 수 있다. Desktop static validator와 runtime template policy가 같은 제한을 적용하며 최종 absolute target은 local profile의 canonical base 안에서만 만들어진다.

`CapabilityIngressPolicy`는 manifest, instructions, workflow, input/settings schema와 test payload를 canonical JSON으로 검사한다. Absolute HTTP(S), endpoint/baseUrl/profileId/auth/credential target 문맥과 명백한 scheme/host/port 조합은 catalog submit/activate/rollback, ZIP import, persisted/runtime snapshot과 Workflow orchestration에서 반복 거부한다. `$schema` URI, logical slot/kind, `connectionSlot`과 relative template은 허용한다. 이 정책은 구조적 금지와 명백한 literal을 방어하는 lint이며 임의 alias, 자연어 은어, 분할·다중 인코딩을 해독하는 증명은 아니다. 따라서 package author와 import source는 검토된 신뢰 경계로 남고 Desktop은 local profile 값을 package에 주입하지 않는다.

Desktop은 exact Capability ID/version/content hash와 각 slot ordinal의 local binding을 결합한다. slot은 ordinal 기준으로 canonical 정렬하며 empty requirement도 별도의 결정적 hash를 가진다. 후보 전체의 aggregate는 runtime snapshot v2의 `connectionBindingHash` 한 값으로만 Sidecar에 보낸다.

SQLite의 `capability_connection_bindings.binding_hash`는 profile ID, endpoint, target hash나 revision에서 결정적으로 계산한 값이 아니다. Desktop은 새 binding generation마다 CSPRNG 32 bytes를 만들고 그 SHA-256 digest를 opaque `binding_hash`로 저장한다. exact capability/slot/kind, profile, captured target/credential identity가 모두 같은 idempotent bind만 현재 row와 generation을 그대로 반환한다. 다른 profile 또는 target/credential identity로 실제 rebind할 때는 binding revision과 opaque generation을 함께 새로 만든다.

Runtime `connectionBindingHash`는 exact Capability ref와 ordinal resolved binding evidence, 이 random opaque generation을 length-prefixed deterministic hash로 결합한 aggregate다. endpoint나 secret의 평문·무염 digest가 아니며 Sidecar와 model은 이 값에서 profile ID, endpoint나 credential을 복원할 수 없다.

Frozen run authorization과 승인 scope는 다음 값을 함께 고정한다.

- snapshot hash와 model Tool name
- exact Capability ID/version/content hash와 catalog revision
- `connectionBindingHash`
- Workflow/input/plan hash, step sequence, canonical Tool/risk와 payload/binding hash

### 변경, readiness 무효화와 frozen call

Endpoint, auth mode 또는 Credential Manager token이 바뀌면 Desktop은 관련 target/credential revision을 증가시키고 성공 probe를 무효화한다. 인증 방식만 `None → Bearer`로 바뀌어도 probe/readiness와 binding identity를 모두 무효화한다. Enabled를 끄거나 다시 켜는 각 변경도 `targetRevision`을 증가시켜 기존 probe와 exact binding을 stale로 만든다. 변경된 profile identity에 exact Capability slot을 다시 bind할 때는 binding revision과 CSPRNG generation을 새로 만든다. Display name만 바뀌고 target/auth/credential identity와 enabled 상태가 같으면 기존 probe와 unchanged binding generation을 보존한다. 실제 SQLite에서 서로 다른 repository가 같은 profile/binding snapshot으로 CAS할 때는 정확히 하나만 성공한다.

이러한 identity/readiness 변경은 다음 상태를 즉시 무효화한다.

- 기존 probe receipt에 기반한 readiness
- runtime candidate와 snapshot hash
- 해당 binding hash에 결합된 approval scope와 아직 소비하지 않은 one-shot ticket

이미 frozen된 호출을 새 profile target이나 새 credential로 전환하지 않는다. Desktop은 Workflow 시작 전과 prepared effect 생성 전에 exact version의 binding, profile target/credential revision, successful probe와 expected `connectionBindingHash`를 다시 비교한다. 실제 송신 단계에서는 공유 lease를 잡고 이 metadata를 선검사한 뒤 credential을 fresh read하고 전체 readiness를 최종 비교하며, lease를 response 처리 종료까지 adapter에 이전한다. Profile save/bind와 probe의 profile-read→credential→network→receipt도 같은 lease를 사용하므로 마지막 비교와 effect 사이에 다른 target/credential 세대가 끼어들 수 없다.

하나라도 달라지면 network byte를 보내기 전에 readiness/admission은 `connection.binding.stale`, HTTP execution은 `connection.http.binding_mismatch`로 거부한다. 사용자는 새 run에서 새 snapshot을 선택하고 target을 다시 검토·승인해야 한다. 변경 전 endpoint를 자동으로 계속 사용하거나 변경 후 endpoint로 silent rebind하는 두 동작 모두 금지한다.

### 첫 수직 Tool

D-4 첫 Tool은 `connection.http.request.v1` 하나다.

- Workflow-only이며 일반 model-facing Desktop Tool로 광고하지 않는다.
- canonical metadata는 `R3 / OneShot / Never / Effect`다.
- method는 `GET`으로 고정한다.
- request body는 없고 model/Sidecar/Workflow input이 absolute URL, scheme, host, port, 임의 header, auth나 token을 제공할 수 없다.
- exact package Workflow가 logical connection slot, relative `pathTemplate`, named path/query parameter의 이름과 bounded 사용자 목적을 고정한다. Parameter 값만 선언된 Workflow input 또는 앞선 step binding에서 채울 수 있으며 runtime에 다시 검증·percent-encode한다.
- 실제 absolute base endpoint와 auth는 local profile에서만 resolve한다. 최종 target은 canonical base와 검증된 relative target의 exact 결합이며 base 밖으로 normalize되거나 이동할 수 없다.
- `IWorkflowOneShotToolAdapter`의 immutable prepared effect와 D-3 single-item ticket 경계를 재사용한다.
- persistent conversation/30-day grant와 자동 retry는 허용하지 않는다.

`connection.http.request.v1`을 Sidecar가 직접 `tool.invoke`하거나 Workflow required Tool 밖에서 호출하면 effect preparation 전에 고정 오류로 거부한다. Package가 `requiredConnections` slot/kind와 `requiredTools`의 이 Tool을 exact하게 선언하지 않거나 binding/readiness가 충족되지 않아도 activation/runtime admission을 fail-closed 한다.

### HTTP transport 경계

Desktop HTTP adapter는 profile의 canonical absolute base endpoint 아래에 package-fixed relative path template과 bounded named parameter로 만든 target 하나에만 GET을 보낸다. Arbitrary absolute URL 조립과 redirect follow는 제공하지 않는다.

- automatic redirect off; 모든 3xx는 `connection.http.redirect_denied`
- cookie store와 cookie replay off
- `UseDefaultCredentials=false`; Windows integrated/default credential off
- proxy와 environment/system proxy off
- arbitrary request header off; adapter가 고정한 protocol header와 선택된 auth mode만 사용
- caller-supplied request body, multipart와 upload off. 실제 request에는 .NET premature-EOF replay 경로를 닫기 위한 adapter-owned non-null 0-byte content만 둔다.
- HTTP/1.1 `RequestVersionExact`와 `Connection: close`를 고정한다. 요청 header/effect가 시작된 뒤 replay하지 않으며 실제 loopback socket에서 header 직후 연결을 끊어도 request와 probe의 wire count가 각각 1임을 검증한다. 요청 byte를 보내기 전의 안전한 connection-establishment retry까지 금지한다고 주장하지 않는다.
- path template은 leading slash, empty segment, `.`, `..`, backslash, URL delimiter와 percent literal을 거부한다. Path/query parameter 이름은 package definition에 고정하고 값은 개별 percent-encode하며 relative target 전체는 UTF-8 2,048 bytes 이하로 제한한다.
- timeout은 승인 대기와 분리한 Desktop-owned 30초 send 상한이며 Tool host 상한은 35초다.
- response header는 16 KiB, decompressed body는 정확히 48 KiB 이하만 허용한다. Declared `Content-Length` N+1은 stream을 읽기 전에 거부하고 exact N은 허용하며, 길이를 모르는 stream도 N+1 byte에서 실패한다.
- content type은 UTF-8 `application/json`, `text/plain`, `+json`만 허용하고 handler 뒤 residual content encoding과 invalid UTF-8을 거부한다.
- structured result text는 최대 16,000 Unicode scalar와 `truncated` 표시를 사용하며 response byte 초과를 성공으로 truncate하지 않는다. 모든 성공 result는 `untrusted=true`다.
- 현재 Bearer credential의 exact raw 값 또는 한 번 JSON-escape된 표현이 normalized content type이나 full decoded text에 있으면 output/truncation 전에 전체 response를 `connection.http.credential_reflected`와 빈 output으로 폐기한다.

Bearer token이 필요한 profile은 승인 뒤 execution re-resolution에서 shared lease를 잡고 current metadata/readiness를 먼저 확인한 다음 Credential Manager에서 fresh read한다. Resolver는 credential read 뒤 profile identity와 전체 readiness aggregate를 한 번 더 비교하고, 이 마지막 검사까지 통과한 경우에만 short-lived credential과 lease를 HTTP adapter에 반환한다. Adapter는 fixed `Authorization` header에만 이를 주입하고 bounded response 처리가 끝날 때 lease를 해제한다. token은 prepared-effect metadata, ticket, audit, exception과 diagnostic bundle에 복사하지 않으며 실행이 끝나면 보유 범위를 즉시 폐기한다.

Reflection guard는 configured endpoint가 실수로 되돌려 보낸 raw/JSON credential을 response 결과로 모델에 전달하지 않기 위한 방어다. Percent/base64/분할 등 임의 재인코딩 covert channel을 일반 response 기능과 함께 완전 판별한다고 주장하지 않는다. 사용자가 구성한 endpoint는 검토된 사내 신뢰 경계이며 Bearer는 가능한 경우 `https` profile에서 사용한다. 폐쇄망 호환을 위해 `http`도 허용하지만 평문 전송 위험은 운영 정책 대상이다.

첫 slice의 timeout, raw response byte, decoded text와 structured result 상한은 contract/adapter 상수로 고정하고 profile이나 package가 늘릴 수 없게 한다. `ConnectionHttpRequestToolTests.ResponseByteLimitAcceptsExactLengthAndRejectsDeclaredOverflowBeforeReading`과 streaming 초과 회귀가 exact `N`/`N+1`, `Content-Length` 선거부를 검증한다. 초과 response를 조용히 truncate해 Workflow가 완전한 결과로 오인하게 하지 않는다.

### 승인과 at-most-once 실행

Desktop preview는 endpoint와 auth 종류를 사용자에게 로컬로 표시할 수 있지만 그 값은 IPC나 audit text에 넣지 않는다. 승인 뒤에는 다음 순서를 지킨다.

```text
resolve exact binding/profile
 → successful probe/readiness 확인
 → immutable request target + payload/binding hash 준비
 → Desktop AllowOnce 승인
 → schema 14 Workflow step reservation + single-item ticket CAS
 → Desktop-wide execution lease 획득
 → binding/profile target·credential revision execution 재검사
 → Credential Manager secret fresh read
 → profile identity와 전체 readiness aggregate 마지막 stale 검사
 → fixed Authorization header 주입
 → GET 한 번 송신
 → bounded result와 credential-reflection 검사
 → lease 해제와 redacted completion 기록
```

마지막 stale 검사 실패, ticket 저장 실패, expiry와 duplicate consumption은 송신 0회다. Network byte를 보내기 전 취소는 `NotStarted`다. 송신이 시작된 뒤 timeout, cancellation 또는 transport 예외로 결과를 확정할 수 없으면 effect를 replay하지 않고 원인 fixed code를 보존한 `Unknown`을 반환해 Workflow를 `attention_required/review_outcome`으로 종료한다. 실제 throwing handler 회귀는 send 1회, step `outcome_unknown/unknown`, run `attention_required/review_outcome`과 raw detail 0건을 고정한다. Approval callback 안 cancellation 회귀는 send 0회, step `not_started`, run `cancelled/retry_from_start`를 고정한다.

### Fixed error와 audit

외부로 노출하는 오류는 bounded fixed code만 사용한다. Profile/readiness 계층은 `connection.binding.missing`, `connection.binding.stale`, `connection.profile.disabled`, `connection.profile.unstable`, `connection.secret.missing`, `connection.secret.unavailable`, `connection.probe.required` 같은 fixed code를 반환한다.

실제 HTTP adapter code와 disposition은 다음 세 집합으로 고정한다.

- network send 전 `NotStarted`: `connection.http.context_required`, `input_invalid`, `binding_unavailable`, `binding_mismatch`, `credential_unavailable`, `binding_invalid`, `expired`, `already_consumed`, `cancelled`
- response를 받은 뒤 결과가 확정된 `FailedKnown`: `connection.http.redirect_denied`, `connection.http.remote_failed`, `connection.http.content_unsupported`, `connection.http.response_too_large`, `connection.http.credential_reflected`
- `SendAsync` 시작 뒤 timeout, cancellation 또는 transport 예외로 결과를 확정할 수 없는 `Unknown`: `connection.http.timeout`, `transport_failed`, 실행 중 `cancelled`

`Unknown`은 별도 `outcome_unknown` 문자열로 덮어쓰지 않는다. 실제 원인 fixed code와 `Unknown` disposition을 함께 유지하며 Workflow를 `attention_required/review_outcome`으로 승격하고 자동 retry하지 않는다.

Workflow ledger는 exact run identity, Tool/risk, canonical payload/binding hash, approval ticket/item·reservation 상태, disposition, bounded output hash, fixed error, redacted generic summary와 타임스탬프만 저장한다. Operational approval audit은 exact Capability context의 bounded digest와 `workflow_once` scope, 일반화된 승인 summary를 저장한다. Endpoint, URL, query, request/response body, header, auth mode의 값, token, Credential Manager target과 raw exception은 로그에 남기지 않는다.

### 후속 executor 소유권

D-4 HTTP 결정을 다른 connector에 암묵적으로 일반화하지 않는다.

- SQL read-only, mail draft/send와 Git read-only는 각각 Desktop-owned typed adapter, canonical target, 별도 input/result contract와 risk ADR을 갖는다.
- SQL 원문, SMTP credential, mail recipient policy, Git executable/repository identity를 Sidecar나 model이 임의 문자열로 정하지 않는다.
- MCP transport/session은 계속 Sidecar가 소유한다. D-4 HTTP profile이나 token을 MCP에 전달하지 않으며, MCP profile/secret broker가 필요하면 기존 MCP security boundary와 호환되는 별도 결정을 낸다.
- generic shell, arbitrary script와 Remote Runner는 D-10 전까지 Connection executor 대안이 아니다.

## 검증 기준

- schema 14→15 migration, repository CAS와 secret-free SQLite fixture
- Credential Manager `Exists` 확인 중 managed secret string 0건, 8~1,280자 shared token68 validation, create/update/delete 실패·복구 중 lifecycle/readiness fail-closed
- exact-version slot/kind binding, new/rebind 시 CSPRNG opaque `binding_hash`, unchanged identity에서만 generation 보존, stable ordinal과 empty requirements의 deterministic aggregate hash
- runtime snapshot v2와 approval scope의 exact `connectionBindingHash` 결합
- endpoint/auth/token/binding 변경과 disable/re-enable 각각에서 probe/readiness/snapshot/scope 무효화
- 실제 SQLite profile/binding stale CAS 경쟁에서 exactly one winner
- shared execution lease가 final resolve→HTTP response와 probe profile read→receipt를 감싸고 mutation을 기다리게 하여 frozen call이 새 target/credential 세대로 전환되지 않음
- package contract에 local connection field 0건, 중앙 ingress에서 명백한 endpoint/profile/auth/credential literal 거부, actual Named Pipe payload의 endpoint/profile/auth/token sentinel 0건
- Workflow-only admission, direct Sidecar/model call과 required Tool/slot drift 시 effect 0회
- R3/OneShot/Never/Effect metadata와 persistent grant 0건
- redirect, cookie, default credential, proxy, arbitrary absolute URL/header/caller body 차단; HTTP/1.1 exact + close + 0-byte content로 request-header 이후 replay 0회; package-fixed relative template와 named parameter의 base confinement
- timeout, status, content type, invalid UTF-8, header 16 KiB, declared/unknown-length exact body 48 KiB, text 16,000 Unicode scalar와 actual fixed code/disposition mapping
- response content type/full decoded text의 exact raw/JSON-escaped credential reflection은 output 0건과 fixed `connection.http.credential_reflected`
- ticket tamper/expiry/duplicate/reservation failure 시 HTTP send 0회
- send 전 cancel은 `NotStarted`와 wire send 0회, send 후 미확정 결과는 원인 fixed code를 보존한 `Unknown`, wire send 1회와 `attention_required`이며 effect replay 0회
- 실제 Named Pipe 경계에서 snapshot/Workflow IPC에 endpoint/auth/token이 없는지 확인

구현·표적 회귀와 최종 검증 증거는 `docs/D4_ACCEPTANCE.md`에 정리했다. 2026-08-02 전체 verify는 Sidecar 123, Contracts 18, Application 187, Sidecar Integration 1, Desktop 596, build warning/error 0으로 통과했고 실제 UIA smoke도 IntegratedPages 7, 760×500와 ApplicationErrors 0으로 통과했다.

## 결과

- 공유 Capability와 로컬 endpoint/credential 수명이 분리된다.
- 모델과 Sidecar는 어떤 Connection을 선택했는지 opaque binding hash 외에는 알지 못한다.
- Profile 변경은 다음 run의 새 snapshot으로만 반영되고 이미 frozen된 run은 다른 target으로 이동하지 않는다.
- HTTP effect는 기존 Desktop policy와 D-3 one-shot ledger를 재사용하면서 network-specific 기본 동작을 fail-closed 한다.
- Package lint와 credential reflection guard의 보장 범위를 명백한 literal/raw·JSON 표현으로 제한하고, package author와 configured endpoint review를 숨기지 않은 운영 신뢰 경계로 남긴다.

## 대안

- Cline Sidecar에서 fetch/axios로 직접 호출하는 방식은 secret, endpoint, 정책과 audit 소유권을 깨므로 채택하지 않는다.
- Package에 base URL, profile ID, auth header나 token placeholder를 넣는 방식은 공유 경계와 로컬 binding을 깨므로 채택하지 않는다.
- model이 URL, method, header나 body를 자유롭게 제출하는 generic HTTP Tool은 target과 승인 scope를 넓히므로 채택하지 않는다.
- Profile 변경 뒤 frozen 호출을 새 target에 자동 rebind하는 방식은 사용자가 검토한 대상과 실제 송신 대상을 다르게 하므로 채택하지 않는다.
- redirect, default credential, cookie나 system proxy를 플랫폼 기본값에 맡기는 방식은 숨은 외부 target과 credential 전파를 만들 수 있어 채택하지 않는다.
- read-only처럼 보이는 GET을 R0/R1로 낮추거나 자동 retry하는 방식은 서버 의미를 Desktop이 증명할 수 없으므로 첫 slice에서는 채택하지 않는다.
