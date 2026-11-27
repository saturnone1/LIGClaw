# D-6C 계획 — typed SMTP fallback과 Desktop-owned attachment snapshot

> 상태: D-6C0~2 text-only reference 구현 완료 · 메일 고급 확장 동결 · D-6C3/4 보류
> 기준일: 2026-08-02
> 완료 기준선: D-6A no-send draft, D-6B fixed Mail Gateway 인수 완료
> 현재 기준: Protocol 1.23 · SQLite schema 20
> 관련 결정: `adr/0041-desktop-owned-mail-draft-and-delivery-semantics.md`, `adr/0042-fixed-mail-gateway-and-durable-submission-journal.md`, `adr/0043-typed-smtp-fallback-and-data-outcome-ambiguity.md`, `adr/0044-desktop-owned-attachment-snapshots-and-mail-contracts.md`

## 1. 결론

D-6C는 D-6B의 Gateway를 일반 SMTP나 임의 파일 전송으로 넓히지 않는다. 다음 다섯 수직 단계를 순서대로 닫는다.

| 단계 | Protocol | DB schema | 범위 | 상태 |
|---|---:|---:|---|---|
| D-6C0 | 1.23 유지 | 19 | D-6B unresolved 조회의 partial-index/query-plan 성능 보정만 수행 | 구현 완료 · 최종 검증 대기 |
| D-6C1 | 1.23 유지 | 20 | 별도 `smtp` Connection, credential, probe, binding과 SMTP journal metadata | 구현 완료 · 최종 검증 대기 |
| D-6C2 | 1.23 유지 | 20 | text-only `mail.send.v1`의 typed SMTP 실행과 DATA ambiguity UX | 구현 완료 · 최종 검증 대기 |
| D-6C3 | 구현 시점 next available | 20 유지 | 별도 attachment prepare/send 계약과 메모리 snapshot 준비; attachment send는 아직 unavailable | 보류 |
| D-6C4 | D-6C3 protocol 유지 | 구현 시점 next available | attachment-bound journal identity와 SMTP MIME 전송 수직 인수 | 보류 |

`fallback`은 관리자가 명시적으로 `smtp` kind를 요구하는 별도 Capability version을 구성한다는 뜻이다. 실행 중 Mail Gateway 실패나 `outcome_unknown`을 보고 SMTP로 자동 전환하지 않는다. 한 Capability의 exact connection kind도 모델이 선택하거나 바꿀 수 없다.

이 단계의 메일은 제품의 최종 목적이 아니라 LLM에 새 기능을 추가하는 공통 경로를 검증하는 reference Capability다. D-6C2의 text-only SMTP에서 구현을 동결하고 더 이상의 mail-specific hardening이나 기능 확장을 현재 목표에 넣지 않는다. 다음 작업은 Builder가 만든 비메일 Capability의 review→activation→hot reload→실행 E2E와 Operations UX다. D-6C3/4는 폐기하지 않되 실제 별도 요구가 생길 때까지 보류한다.

## 2. 변경하지 않는 경계

- Desktop이 SMTP socket, TLS, Credential Manager, file picker, 파일 byte, approval, journal, retry 판정과 SQLite를 소유한다.
- Sidecar/Cline은 SMTP session, MIME, file path, attachment name/byte, credential과 journal을 소유하지 않는다.
- Domain/Application은 WPF, SQLite, SMTP client, filesystem handle과 Windows adapter를 참조하지 않는다.
- D-6A `mail.create_draft.v1`과 D-6B `mail.send.v1`에 attachment, sender, endpoint, header 또는 auth field를 추가하지 않는다.
- text-only SMTP는 현재 Protocol 1.23 `mail.send.v1` input/result를 byte-for-byte 유지한다.
- attachment는 구현 시점의 next available protocol에서 `mail.prepare_selected_attachments.v1`과 `mail.send_with_attachments.v1`을 별도 계약으로만 연다. Protocol 1.24는 범용 `capability.builder.open.v1`이 사용하므로 attachment 계획이 번호를 예약하지 않는다.
- SMTP와 attachment send는 Workflow-only `R3 / OneShot / Never / Effect` 경계를 우회하지 않는다. attachment prepare도 Workflow-only이며 일반 model Tool로 등록하지 않는다.
- 실행 시점의 mail payload, file path/name/byte와 credential을 outbox, Workflow run/step ledger, activity/audit, diagnostic bundle 또는 Tool result에 추가 복제하지 않는다. 단, 사용자가 채팅에 직접 입력한 문장은 기존 대화/FTS 정책에 따라 SQLite에 남고 모델 context로 전달되며, schema-bound Workflow input도 Sidecar→Desktop IPC를 통과한다. 사용자가 검토·승인한 immutable Capability 정의의 literal도 Catalog에 저장될 수 있다. Sidecar는 이 원문을 durable storage나 diagnostic log에 남기지 않지만, 입력 원본과 IPC transit까지 비영속·비노출이라고 주장하지 않는다.
- generic HTTP POST, arbitrary SMTP command/header, shell, PowerShell와 UIA/webmail 자동화는 이 transport의 대안이 아니다.

## 3. D-6C0 — schema 19 unresolved 조회 성능 preflight

D-6C0는 기능 추가가 아니다. D-6B에서 안전성 blocker가 아니라고 분리한 medium 성능 과제만 먼저 닫는다.

현재 조회는 같은 draft/binding의 `submitting | outcome_unknown`을 `ORDER BY updated_at_utc DESC, operation_id LIMIT 2`로 찾지만 기존 partial composite index는 `outcome_unknown`만 포함한다. `LIMIT 2`는 반환 row와 network GET을 제한할 뿐 극단적으로 큰 journal의 scan/sort 작업을 제한하지 않는다.

schema 19 migration은 다음 query shape를 지원하는 전용 partial index를 transaction 안에서 추가한다.

```text
key:   draft_hash, binding_hash, updated_at_utc DESC, operation_id
where: submission_state IN ('submitting', 'outcome_unknown')
```

구현 시 실제 SQLite 이름과 DDL은 migration test에 고정하되 다음을 지킨다.

- 기존 row, state graph, timestamps, trigger, retention, retry와 reconciliation 의미는 바꾸지 않는다.
- Protocol, generated contract, Tool catalog, Connection, credential, UI와 network 코드는 바꾸지 않는다.
- 새 index 생성과 기존 outcome-only index 정리는 하나의 schema migration으로 복구 가능해야 한다.
- `EXPLAIN QUERY PLAN` fixture는 production query가 새 index를 선택하고 별도 temporary sort에 의존하지 않는지 확인한다.
- 0/1/2/100+ unresolved fixture에서 정렬, state 포함 범위와 반환 결과가 schema 18과 동일해야 한다.
- wall-clock 하나만으로 acceptance를 판정하지 않는다. query plan, 결과 동일성, migration/reopen/backup과 bounded returned-row evidence를 함께 사용한다.

### D-6C0 gate

- [x] schema 18→19 forward migration과 current-schema reopen 통과
- [x] schema 18 snapshot을 migration한 뒤 unresolved query 결과 parity 통과
- [x] `submitting`과 `outcome_unknown`을 모두 포함하는 production query-plan fixture 통과
- [x] journal state/recovery/delete guard와 D-6B replay 무회귀
- [x] release manifest/evidence가 최종 current schema 20을 반영하고 schema 19 migration을 보존
- [ ] 전체 `./scripts/verify.ps1` 통과

D-6C0 gate가 닫히기 전에는 SMTP table이나 dependency를 추가하지 않는다.

## 4. D-6C1 — typed `smtp` Connection과 schema 20

D-6C1은 send adapter를 열기 전에 Connection과 저장 경계를 닫는다. 상세 wire/security 결정은 ADR 0043을 따른다.

### profile과 secret

- `smtp`는 `http`, `mail-api`와 다른 exact connection kind다.
- profile metadata는 canonical ASCII DNS host, fixed port, `implicit-tls | starttls-required`, fixed ASCII sender, enabled/lifecycle, target/credential revision과 target hash만 저장한다.
- 인증 방식은 TLS 안의 `AUTH PLAIN` 하나로 고정한다. username/password 원문은 SMTP 전용 Credential Manager target에만 저장한다.
- system trust와 hostname validation을 사용한다. trust bypass, accept-any-certificate, plaintext downgrade와 opportunistic STARTTLS는 없다.
- `implicit-tls`는 greeting 전 TLS를 완료한다. `starttls-required`는 pre-TLS EHLO에서 STARTTLS를 요구하고 upgrade 후 EHLO를 다시 수행하며 pre-TLS capability를 폐기한다.
- profile/credential/probe/binding mutation과 execution final resolution은 HTTP/Mail Gateway와 동일한 production `ConnectionExecutionLeaseCoordinator`를 공유한다.
- probe는 TLS, post-TLS EHLO, AUTH PLAIN advertisement와 실제 credential authentication까지만 확인하고 `MAIL FROM`, `RCPT TO`, `DATA`를 보내지 않는다.

### schema 20

schema 20은 다음 typed metadata를 추가한다.

- SMTP profile, probe receipt, exact-version local binding table과 stale/CAS/delete trigger
- 기존 mail outbox row의 `transport_kind`를 `mail-api`로 backfill하고 신규 `smtp`를 허용하는 exact constraint
- SMTP operation의 bounded stable Message-ID와 durable phase metadata
- `pre_data | data_started` 경계를 복구할 수 있는 transition/trigger
- SMTP row에도 recipient, sender, subject, body, host, username/password, raw response와 MIME byte column이 없음을 보장하는 schema test

schema 20은 Protocol 1.23 계약을 변경하지 않는다. attachment field나 attachment Tool 이름을 미리 허용하지 않는다.

### D-6C1 gate

- [x] Protocol 1.23 schema/hash/generated C#/TypeScript가 D-6B와 동일함을 증명
- [x] schema 19→20 migration, mail-api backfill, SMTP CRUD/CAS/reopen/recovery 통과
- [x] SMTP credential namespace 분리와 DB/WAL/backup/raw diagnostic canary 0건
- [x] TLS trust/hostname/STARTTLS downgrade/AUTH mechanism negative probe replay 통과
- [x] HTTP, Mail Gateway와 SMTP가 동일 cross-kind lease instance를 쓰는 production composition test 통과
- [x] Connections/Capability review의 typed `smtp` save/probe/rebind/readiness UIA 통과
- [x] D-6C1 단계에서는 SMTP send adapter가 runtime unavailable이고 probe의 envelope/DATA가 0회였음을 회귀로 보존
- [ ] 전체 verify와 UI smoke 통과

## 5. D-6C2 — Protocol 1.23 text-only SMTP send

D-6C2는 기존 `mail.send.v1`의 exact input/result와 D-6A `MailDraftPolicy`를 재사용한다. Capability manifest의 exact connection requirement가 `mail-api`이면 D-6B Gateway adapter, `smtp`이면 SMTP adapter를 선택한다. Workflow/model은 transport, host, port, TLS mode, auth, sender, Message-ID와 retry를 입력하지 않는다.

SMTP 실행은 다음 순서를 고정한다.

```text
AllowOnce 승인과 one-shot ticket
 → exact Capability/draft/binding/readiness final resolution
 → metadata-only operation stage
 → SMTP TLS/EHLO/AUTH
 → MAIL FROM과 모든 RCPT TO 확인
 → 한 recipient라도 거부되면 RSET/known failure, DATA 0회
 → server 354 뒤 data_started durable CAS
 → deterministic text/plain MIME + terminating dot
 → exact final acceptance면 queued
 → DATA 시작 뒤 응답 불명확이면 outcome_unknown, retry/reconcile 0회
```

- initial SMTP network retry budget은 0이다.
- SMTP `queued`는 final positive DATA completion reply를 받은 server acceptance이며 delivery evidence가 아니다. D-6C SMTP는 `delivered`를 만들지 않는다.
- stable Message-ID는 operation을 SMTP/server log에서 찾기 위한 진단 identity일 뿐 idempotency key, receipt 또는 exactly-once 증거가 아니다.
- SMTP에는 D-6B Gateway와 같은 operation lookup API가 없다. DATA 이후 `outcome_unknown`을 자동 GET, 재접속, 같은 Message-ID 재전송 또는 새 transport fallback으로 조정하지 않는다.
- 같은 draft hash의 unresolved SMTP row는 binding이 달라도 새 submission을 보수적으로 막는다. D-6C는 user override나 “보내지지 않음으로 간주” transition을 제공하지 않으며 운영자는 Message-ID로 서버 측 결과를 확인한다.
- Bcc 주소는 envelope에만 있고 MIME header에는 쓰지 않는다. 모든 recipient가 envelope에서 accept된 뒤에만 DATA를 시작한다.
- remote SMTP text와 enhanced status 원문은 결과/로그에 남기지 않고 fixed local code로만 변환한다.
- SMTP reply line/count/aggregate byte와 전체 transaction 30초 operation deadline은 bounded하며 initial adapter는 PIPELINING을 사용하지 않는다.

### D-6C2 gate

- [x] existing `mail.send.v1` parser/static dominance/runtime result parity와 mail-api 무회귀
- [x] ordinary Tool/Sidecar direct call, transport-kind mismatch와 stale binding이 socket 0회인지 확인
- [x] TLS/EHLO/AUTH/envelope/DATA phase별 fragmentation·cancel, DATA 전/후 30초 global deadline, crash와 fixed error replay
- [x] 일부 RCPT reject 시 DATA 0회, partial recipient delivery 0건
- [x] data_started 전 failure는 known-not-submitted, data_started 뒤 missing final reply는 outcome_unknown인지 확인
- [x] outcome_unknown 뒤 reconnect/retry/reconcile/fallback 0회와 startup recovery 결정성 확인
- [x] final SMTP acceptance가 `queued`만 반환하고 `delivered`를 반환하지 않는지 확인
- [x] binding-only Capability와 비민감 대화 입력을 사용한 실제 실행에서 invocation-only recipient/body/credential/server-text canary가 result/outbox/workflow ledger/activity/audit/DB/WAL/diagnostic에 추가 복제되지 않는지 확인
- [ ] full-mail approval, Activity attention-required와 keyboard/UIA smoke 통과
- [ ] 전체 verify를 통과한 build만 text-only SMTP 인수·배포 대상으로 승격

## 6. D-6C3 — next available protocol의 attachment contract와 snapshot prepare

구현 시점의 next available protocol은 기존 v1 Tool에 path field를 추가하지 않고 다음 두 계약을 동시에 생성한다. 상세 file/snapshot 결정은 ADR 0044를 따른다.

### `mail.prepare_selected_attachments.v1`

Input은 additional property가 없는 empty object다. 모델이나 Workflow가 path, filename, URI, glob, directory 또는 byte를 제공하지 않는다. Desktop system file picker에서 사용자가 직접 고른 파일만 받는다.

Result는 다음 bounded metadata만 가진다.

- `attachmentSnapshotId`: Desktop이 발급한 opaque `mas_` identity
- `attachmentManifestHash`: exact `sha256:` lowercase hash
- `attachmentCount`
- `totalBytes`

path, filename, per-file hash, media type와 byte는 result에 없다. adapter는 Workflow-only `R1 / OneShot / Never / Observation`이며 persistent/conversation grant를 허용하지 않는다.

### `mail.send_with_attachments.v1`

Input은 다음 exact field만 허용한다.

- `connectionSlot`
- `draftHash`
- `attachmentSnapshotId`
- `attachmentManifestHash`
- `to`, `cc`, `bcc`, `subject`, `body`

static validator는 draft hash가 지배하는 `mail.create_draft.v1` result의 exact binding이고 두 attachment field가 같은 지배적 `mail.prepare_selected_attachments.v1` result의 exact binding인지 확인한다. initial D-6C는 literal `smtp` connection slot만 허용한다.

Result는 `operationId`, `status`, `draftHash`, `attachmentManifestHash`만 가진다. initial SMTP result의 status는 `queued`만 성공이다. 이 계약은 `R3 / OneShot / Never / Effect`, Workflow-only이며 D-6C3에서는 adapter를 runtime unavailable로 유지한다.

### D-6C3 snapshot bounds

- local fixed NTFS/ReFS regular file만 허용한다.
- UNC, device path, alternate data stream, directory와 모든 path segment/file의 reparse point를 거부한다.
- cloud placeholder/offline/recall file과 filesystem type을 확정할 수 없는 대상은 fail closed한다.
- hard limit은 파일 8개, 개별 8 MiB, aggregate raw byte 16 MiB다.
- 한 workflow run에 active snapshot 1개, process 전체 active snapshot 2개/aggregate 32 MiB로 제한한다.
- snapshot TTL은 trusted clock 기준 5분이며 restart, expiry, cancel, replacement와 run completion에서 폐기한다.
- file identity, leaf filename, path와 byte는 Desktop process memory에만 둔다. byte buffer는 reject/expiry/cancel/completion/dispose/shutdown에서 zeroize한다.
- leaf filename은 NFC-normalized Unicode scalar 255개/UTF-8 1,024 byte로 제한하고 control/CR/LF/NUL과 snapshot 내 대소문자 무시 중복을 거부한다.
- 해당 신규 protocol 계약은 snapshot metadata만 연다. D-6C3는 network, SMTP DATA, journal insert가 0회다.

### D-6C3 gate

- [ ] 구현 시점 신규 protocol의 C#/TypeScript generation parity와 historical Protocol 1.23/1.24 fixture 보존
- [ ] 두 계약의 exact additional-property/path-field negative test 통과
- [ ] local fixed NTFS/ReFS, UNC/device/ADS/reparse/cloud-placeholder policy replay 통과
- [ ] count/per-file/aggregate/process/TTL bound와 concurrent prepare race test 통과
- [ ] handle identity, before/after read metadata, content hash와 source revalidation mismatch가 fail closed인지 확인
- [ ] path/name/byte canary가 result, workflow/audit, SQLite/WAL/backup, diagnostic와 Sidecar IPC에 0건인지 확인
- [ ] 모든 terminal/cancel/expiry/replacement/shutdown 경로에서 buffer zeroization 확인
- [ ] picker/preview keyboard, screen-reader, 760×500 compact UIA 통과
- [ ] attachment send unavailable, network/journal effect 0회와 전체 verify 통과

## 7. D-6C4 — attachment-bound SMTP send와 next available schema

Attachment send는 durable operation identity에 manifest를 포함해야 한다. schema 20 trigger를 version 없이 바꾸지 않고 구현 시점의 next available schema에서 다음 metadata-only 변경을 수행한다. schema 21은 이후 범용 Builder semantic fixture 저장 경계가 사용했으므로 attachment 계획이 번호를 예약하지 않는다.

- `mail.send_with_attachments.v1 / R3 / Never / Effect` exact step만 허용하는 journal trigger
- non-null `attachment_manifest_hash`, `attachment_count`, `attachment_total_bytes`와 transport kind `smtp` binding
- text-only row에서는 attachment metadata가 모두 null이고 attachment row에서는 모두 exact인 mutual constraint
- same tool-call replay identity에 draft, binding과 attachment manifest metadata를 모두 포함
- path, filename, file identity와 MIME/attachment byte column은 계속 금지

prepared approval은 connection/sender, 전체 recipients, subject/body와 attachment filename/size/total을 Desktop memory에서만 스크롤 가능하게 보여 준다. grant/ticket/payload binding은 exact draft hash, attachment manifest hash, snapshot ID, local connection binding과 execution fingerprint를 포함한다.

실행 직전 Desktop은 source identity와 content hash를 다시 확인한다. 변경됐으면 snapshot을 zeroize하고 reselection/reapproval을 요구한다. 승인 후에는 경로를 다시 따라가지 않고 승인된 immutable snapshot byte만 streaming base64 MIME encoder에 전달한다.

- attachment Content-Type은 initial slice에서 `application/octet-stream`으로 고정한다.
- deterministic MIME는 CRLF, header injection 차단, encoded filename, Bcc header 제외, dot-stuffing과 SMTP line bound를 적용한다.
- 별도 full-message string/base64 copy를 만들지 않고 bounded zeroizable buffer로 stream한다.
- encoded MIME 전체 상한은 24 MiB이며 server가 EHLO `SIZE`를 광고하면 더 작은 한도를 적용한다.
- DATA ambiguity, Message-ID, no-retry/no-reconcile 의미는 text-only SMTP와 동일하다.
- completion/unknown/failure/cancel 뒤 snapshot byte와 encoder buffer를 zeroize한다.

### D-6C4 gate

- [ ] schema 20→21 migration/reopen/recovery/trigger와 release manifest fixture 통과
- [ ] draft + attachment manifest + binding + tool-call exact replay identity와 mismatch network 0회
- [ ] approval 전 source 변경/reparse/identity/hash mismatch가 SMTP 0회인지 확인
- [ ] MIME wire fixture가 recipients/Bcc/header encoding/dot-stuff/line/wire-size exact인지 확인
- [ ] partial RCPT, DATA known reject, ambiguous final reply와 crash replay가 D-6C2 상태 의미를 유지
- [ ] attachment path/name/byte가 SMTP wire와 approval UI 외의 result/log/ledger/DB/WAL/diagnostic에 0건인지 확인
- [ ] completion/unknown/cancel/dispose/restart에서 snapshot 및 transient buffer zeroization 확인
- [ ] 실제 Workflow → picker → prepare → full approval → SMTP queued 수직 회귀 통과
- [ ] 760×500 UI smoke, 전체 verify, contract/release evidence 통과 뒤에만 runtime available

## 8. 단계 간 승격 규칙

- 각 단계는 별도 vertical change와 acceptance 문서를 가진다. 앞 단계의 전체 verify가 통과하기 전에 다음 schema/protocol을 시작하지 않는다.
- schema migration, protocol bump, Connection runtime availability와 UI activation을 한 commit의 암묵적 부수 효과로 섞지 않는다.
- attachment용 신규 protocol이 생성돼도 D-6C4 gate 전에는 `mail.send_with_attachments.v1`이 실행 가능하다고 advertise하지 않는다.
- D-6C1 profile이 존재해도 D-6C2 gate 전에는 `mail.send.v1`의 `smtp` requirement를 runtime ready로 판정하지 않는다.
- 신규 schema가 필요 없다는 구현 변경이 생기면 trigger/replay identity를 어떻게 version 없이 안전하게 보장하는지 별도 ADR로 먼저 증명한다.

## 9. 의도적으로 미지원

- Mail Gateway → SMTP 또는 SMTP → Mail Gateway 자동 fallback
- SMTP DATA outcome-unknown의 자동 resend, reconnect, reconcile, same-Message-ID retry와 user override
- plaintext SMTP, opportunistic STARTTLS, trust bypass, custom certificate accept toggle, anonymous/LOGIN/NTLM/GSSAPI auth
- SMTPUTF8 address, DSN, delivery/read receipt, server-side delivered 판정
- HTML, arbitrary header, reply-to, inline/CID image, calendar invite, S/MIME/sign/encrypt
- attachment를 기존 `mail.create_draft.v1` 또는 `mail.send.v1`에 추가하는 방식
- Mail Gateway v1 attachment 전송; 필요하면 Gateway v2와 별도 계약/ADR
- UNC/network/removable/non-NTFS/ReFS, reparse/symlink/junction, ADS, directory, glob, cloud placeholder
- disk spool, temp file, SQLite blob, encrypted offline payload queue와 restart-resumable attachment snapshot
- archive 자동 생성/해제, malware scanning 또는 DLP를 제공한다고 주장하는 것
- stable Message-ID를 exactly-once나 delivery evidence로 사용하는 것

## 10. D-6C 완료 정의

D-6C는 D-6C0~D-6C4의 각 gate, 전체 verify, UI smoke와 protocol/release evidence가 모두 통과한 뒤에만 완료다. 문서 작성, schema 예약, contract 생성 또는 SMTP profile UI만으로 완료라 표시하지 않는다.
