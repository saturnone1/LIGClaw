# ADR 0043: typed SMTP fallback과 DATA outcome ambiguity

> 상태: 채택 · D-6C1/2 구현 완료 · 최종 전체 검증 대기
> 기준일: 2026-08-02
> 관련 계획: `../D6C_PLAN.md`
> 선행 결정: `0041-desktop-owned-mail-draft-and-delivery-semantics.md`, `0042-fixed-mail-gateway-and-durable-submission-journal.md`

## 맥락

D-6B의 고정 Mail Gateway는 operation lookup과 idempotency key를 사용해 불명확한 HTTP 결과를 조정할 수 있다. 일부 폐쇄망에는 이 Gateway가 없고 조직 SMTP relay만 있지만, SMTP는 `DATA`를 보낸 뒤 연결이 끊기면 서버가 메시지를 수락했는지 클라이언트가 확정할 표준 조회 수단이 없다. 같은 메시지를 재전송하면 중복 메일이 발생할 수 있고, 보내지 않았다고 단정하면 실제 접수된 메일을 숨길 수 있다.

SMTP를 generic socket이나 Gateway 장애 시 자동 fallback으로 열면 Connection kind, 승인, sender와 retry 정책을 모델이 간접적으로 선택하게 된다. 따라서 transport 선택과 DATA 전후의 durable 상태를 명시적으로 고정해야 한다.

## 결정: fallback은 별도 typed Connection 선택이다

- `smtp`는 `mail-api`, `http`와 다른 exact Connection kind다.
- 이 ADR에서 fallback은 관리자가 특정 Capability version의 connection requirement를 `smtp`로 명시한다는 뜻이다. Mail Gateway 실패, timeout 또는 `outcome_unknown`을 보고 실행 중 SMTP로 자동 전환하지 않는다. 반대 방향 자동 전환도 없다.
- D-6C2 text-only send는 Protocol 1.23의 기존 `mail.send.v1` input/result를 변경하지 않는다. static validator는 manifest가 요구하는 literal connection slot과 exact kind를 검증하고 Desktop이 해당 kind의 adapter를 선택한다. transport, host, port, sender와 auth는 Tool payload가 아니다.
- D-6B의 `mail-api` behavior와 historical contract fixture는 그대로 유지한다.
- SMTP execution과 profile mutation은 HTTP/Mail Gateway와 동일한 production `ConnectionExecutionLeaseCoordinator`를 사용한다. 서로 다른 kind도 final resolution과 mutation 구간을 동시에 통과하지 않는다.

## 결정: 고정 SMTP security profile

Desktop-owned SMTP profile에는 canonical ASCII DNS host, fixed port, `implicit-tls | starttls-required`, fixed ASCII envelope sender, lifecycle, target/credential revision과 target hash만 저장한다. secret은 SMTP 전용 Credential Manager target의 username/password로 분리한다.

- TLS는 system trust와 hostname validation을 사용한다. 폐쇄망 CA가 필요하면 운영자가 OS trust store에 설치한다.
- `implicit-tls`는 greeting 전에 TLS handshake를 완료한다. `starttls-required`는 unauthenticated EHLO에서 STARTTLS를 exact하게 확인하고 즉시 upgrade한 뒤 EHLO를 다시 수행하며, upgrade 전 capability는 모두 폐기한다.
- certificate bypass, accept-any-certificate, plaintext SMTP, opportunistic STARTTLS와 TLS downgrade는 허용하지 않는다.
- 인증은 TLS가 성립한 뒤 `AUTH PLAIN`만 허용한다. anonymous, LOGIN, CRAM, NTLM, GSSAPI 또는 모델이 고르는 mechanism은 지원하지 않는다.
- probe는 TLS, post-TLS EHLO, `AUTH PLAIN` advertisement와 실제 credential authentication까지만 수행한다. `MAIL FROM`, `RCPT TO`, `DATA`는 probe에서 절대 보내지 않는다.
- greeting/reply parser는 line length, multiline count와 aggregate byte를 고정하고 전체 probe/submission에 30초 global operation deadline을 둔다. response code가 섞인 multiline reply, oversized input과 unsolicited command flow는 fail closed하며 initial adapter는 SMTP PIPELINING을 사용하지 않는다.
- server greeting, EHLO capability와 reply text 원문은 저장하거나 사용자 결과에 넣지 않는다. fixed local code와 bounded metadata만 사용한다.

## 결정: deterministic text-only SMTP transaction

SMTP adapter는 D-6A `MailDraftPolicy`를 통과한 ASCII mailbox와 plain-text content만 사용한다. fixed sender와 모든 recipient는 Desktop의 exact Capability/Connection binding에서 해석한다.

1. AllowOnce approval과 one-shot ticket을 소비하고 exact draft/binding/readiness를 final resolve한다.
2. network 전에 metadata-only operation을 durable하게 stage한다.
3. TLS, EHLO와 AUTH를 완료한 뒤 `MAIL FROM`과 모든 `RCPT TO`를 확인한다.
4. 한 recipient라도 거부되면 가능한 경우 `RSET`하고 known failure로 끝낸다. 이 경우 `DATA`는 0회다.
5. 모든 recipient가 수락됐을 때만 `DATA`를 요청한다. `354`를 받은 뒤 첫 message byte를 쓰기 전에 durable `data_started` CAS를 완료한다.
6. deterministic `text/plain; charset=utf-8` MIME를 CRLF, header injection 방지, Bcc header 제외, dot-stuffing과 SMTP line bound를 적용해 전송한다.
7. terminating dot에 대한 exact final positive completion reply만 `queued`로 기록한다.

`queued`는 해당 SMTP server가 메시지를 접수했다는 증거일 뿐 최종 delivery 증거가 아니다. D-6C SMTP는 `delivered`를 만들지 않는다. remote enhanced status와 free-form reply text는 redacted fixed result로만 변환한다.

## 결정: schema 20과 durable phase

schema 20은 SMTP profile/probe/binding metadata와 기존 submission journal의 typed SMTP operation metadata를 추가한다.

- 기존 D-6B row는 `transport_kind = mail-api`로 backfill하고 신규 row에는 exact `mail-api | smtp` constraint를 둔다.
- SMTP row에는 bounded stable Message-ID와 `pre_data | data_started` phase를 저장한다.
- SMTP outbox/profile/probe/binding에는 recipient, subject, body, username/password, raw SMTP reply와 MIME byte column을 두지 않고, 실행 payload를 Workflow ledger, activity/audit 또는 diagnostic에 추가 복제하지 않는다. profile의 host와 fixed sender는 비밀이 아닌 연결 metadata로 저장한다.
- D-6B internal idempotency key는 SMTP wire로 보내지 않으며 SMTP idempotency를 주장하는 근거로 사용하지 않는다.
- state/phase transition은 CAS와 trigger로 고정하고, startup recovery가 동일 row에서 같은 판단을 반복하도록 한다.

stable Message-ID는 operation을 SMTP/server log에서 찾기 위한 진단 identity다. receipt, idempotency key, exactly-once 또는 delivery 증거가 아니다.

## 결정: DATA 이후에는 안전한 자동 복구가 없다

initial SMTP network retry budget은 0이다.

| 관찰 가능한 경계 | durable 결과 | 자동 동작 |
|---|---|---|
| `data_started` 전 connect/TLS/EHLO/AUTH/envelope 실패 | known-not-submitted failure | retry 0회 |
| 모든 RCPT 수락 전 일부 recipient 거부 | known-not-submitted failure | `DATA` 0회 |
| `data_started` 뒤 exact final positive reply | `queued` | 추가 send 0회 |
| `data_started` 뒤 exact final negative reply | known failure | retry 0회 |
| `data_started` 뒤 timeout, cancel, disconnect, crash, missing/malformed reply | `outcome_unknown` | retry/reconcile/fallback 0회 |

`outcome_unknown`에는 자동 reconnect, 같은 Message-ID 재전송, 새 Message-ID 전송, Mail Gateway fallback 또는 SMTP 조회라고 부르는 추측성 동작을 하지 않는다. 같은 draft hash의 unresolved SMTP operation은 binding이 달라도 새 submission을 보수적으로 막는다. D-6C는 user override나 “보내지지 않음으로 간주” transition을 제공하지 않는다. 운영자는 stable Message-ID로 서버 운영 로그를 확인해야 한다.

승인 화면은 full mail content를 승인 시점의 Desktop memory에서만 보여 준다. Activity는 ambiguity가 생기면 “접수 여부 확인 필요”와 Message-ID만 표시한다. content, credential, raw server text는 workflow result, audit, diagnostic과 journal에 노출하지 않는다.

이 실행 경계는 기존 제품 저장·모델 경계를 바꾸지 않는다. 사용자가 채팅에 직접 입력한 문장은 conversation/FTS에 영속되고 모델 context로 전달되며, schema-bound Workflow input은 Sidecar→Desktop IPC를 통과한다. 검토·승인한 immutable Capability 정의가 literal recipient/subject/body를 포함하면 Catalog에도 영속된다. Sidecar는 원문을 durable storage나 diagnostic log에 남기지 않는다. D-6C의 비복제 보장은 이 입력/정의 원본과 schema-bound transit을 제외한 실행 outbox/ledger/activity/audit/diagnostic/result에 적용된다.

## 단계별 인수 조건

### D-6C1

- schema 19→20 migration, mail-api backfill, SMTP CRUD/CAS/reopen/recovery가 결정적이어야 한다.
- TLS trust/hostname, STARTTLS downgrade와 unsupported AUTH negative fixture가 socket phase별로 고정돼야 한다.
- probe가 envelope와 DATA를 보내지 않으며 credential/content canary가 DB/WAL/backup/diagnostic에서 0건이어야 한다.
- Protocol 1.23 schema/hash와 generated C#/TypeScript는 D-6B 기준과 동일해야 한다.
- D-6C1에서는 SMTP send adapter가 unavailable이고 send socket 호출이 0회여야 한다.

### D-6C2

- 기존 `mail.send.v1` static dominance, parser와 mail-api replay가 무회귀여야 한다.
- fragmentation, timeout, cancel과 crash를 TLS/EHLO/AUTH/envelope/DATA phase별 deterministic replay로 검증해야 한다.
- partial RCPT reject는 DATA 0회이고, `data_started` 뒤 ambiguous outcome은 재접속/retry/reconcile/fallback 0회여야 한다.
- final positive SMTP reply는 `queued`만 만들고 `delivered`를 만들지 않아야 한다.
- full-mail approval, Activity attention-required UX, keyboard/UIA와 전체 verify를 통과한 build만 인수·배포 대상으로 승격한다. 이는 별도 runtime feature flag를 뜻하지 않는다.

## 의도적으로 미지원

- runtime transport fallback 또는 transport를 Tool input으로 고르는 기능
- SMTP DATA ambiguity의 자동 resend/reconcile와 user override
- plaintext/opportunistic TLS, trust bypass와 `AUTH PLAIN` 이외 인증
- SMTPUTF8, DSN, delivery/read receipt와 server-side delivery 판정
- HTML, arbitrary header, reply-to, inline/CID, calendar, S/MIME
- stable Message-ID를 exactly-once 또는 delivery evidence로 사용하는 방식

## 결과

- 폐쇄망 SMTP는 Gateway와 독립된 typed transport로 추가되며 기존 Protocol 1.23 mail 계약을 넓히지 않는다.
- DATA 전후의 durable 경계가 복구 때도 동일한 conservative 결과를 만든다.
- 불명확한 결과를 자동 전송으로 덮지 않으므로 중복 메일 위험이 운영자에게 명시적으로 드러난다.
- SMTP에는 Gateway와 같은 reconcile 능력이 없다는 사실을 UI와 상태 의미가 정직하게 반영한다.

## 대안

- Gateway 실패 시 SMTP로 자동 fallback하는 방식은 한 승인으로 서로 다른 외부 효과를 만들 수 있어 채택하지 않는다.
- timeout 후 같은 Message-ID를 재전송하는 방식은 Message-ID가 idempotency key가 아니므로 채택하지 않는다.
- 모든 SMTP 오류를 known failure로 기록하는 방식은 DATA 이후 실제 접수를 숨길 수 있어 채택하지 않는다.
- SMTP reply text를 journal에 저장하는 방식은 content와 운영 정보 유출 위험이 있어 채택하지 않는다.
