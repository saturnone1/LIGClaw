# ADR 0044: Desktop-owned attachment snapshot과 mail contracts

> 상태: 채택 · D-6C3/4 설계 승인 · 미구현
> 기준일: 2026-08-02
> 관련 계획: `../D6C_PLAN.md`
> 선행 결정: `0041-desktop-owned-mail-draft-and-delivery-semantics.md`, `0043-typed-smtp-fallback-and-data-outcome-ambiguity.md`

## 맥락

메일 첨부를 기존 `mail.create_draft.v1` 또는 `mail.send.v1`에 path field로 추가하면 모델/Workflow가 임의 파일을 지목할 수 있다. 경로 문자열만 승인하면 reparse point, hard link, cloud recall과 승인 후 변경 때문에 실제 전송 byte가 달라질 수 있다. 파일 이름, 경로와 byte를 Sidecar나 durable store에 넘기면 local-first content 경계도 깨진다.

따라서 사용자가 Desktop picker에서 직접 선택한 bounded byte만 immutable snapshot으로 준비하고, 초안과 snapshot identity를 별도 effect 계약에서 결합한다. 기존 v1 계약은 변경하지 않는다.

## 결정: 구현 시점 next available protocol의 별도 두 계약

D-6C3는 구현 시점의 next available protocol에서 다음 두 canonical Tool 계약을 동시에 추가한다. Protocol 1.24는 이후 범용 `capability.builder.open.v1`이 사용했으므로 이 동결 계획은 protocol 번호를 예약하지 않는다. 기존 Protocol 1.22/1.23/1.24 historical fixture와 `mail.create_draft.v1`, `mail.send.v1` schema에는 attachment 관련 field를 절대 추가하지 않는다.

### `mail.prepare_selected_attachments.v1`

Input은 `additionalProperties: false`인 empty object다. path, filename, URI, glob, directory, media type 또는 byte를 모델/Workflow가 제공할 수 없다. Desktop system file picker가 유일한 선택 진입점이다.

성공 result에는 다음 필드만 있다.

- `attachmentSnapshotId`: Desktop이 발급한 opaque `mas_` identity
- `attachmentManifestHash`: exact lowercase `sha256:` hash
- `attachmentCount`
- `totalBytes`

path, filename, file identity, per-file hash, media type와 byte는 result에 없다. 이 adapter는 Workflow-only `R1 / OneShot / Never / Observation`이며 persistent/conversation grant를 허용하지 않는다.

### `mail.send_with_attachments.v1`

Input은 다음 exact field만 허용한다.

- `connectionSlot`
- `draftHash`
- `attachmentSnapshotId`
- `attachmentManifestHash`
- `to`, `cc`, `bcc`, `subject`, `body`

static validator는 draft 관련 필드가 같은 지배 경로의 `mail.create_draft.v1` result와 exact하게 결합됐는지, 두 attachment field가 같은 지배 경로의 `mail.prepare_selected_attachments.v1` result와 exact하게 결합됐는지 확인한다. initial D-6C는 literal `smtp` connection slot만 허용한다.

성공 result에는 `operationId`, `status`, `draftHash`, `attachmentManifestHash`만 있다. SMTP 성공 status는 `queued`뿐이다. 이 adapter는 Workflow-only `R3 / OneShot / Never / Effect`다. D-6C3에서는 계약과 snapshot prepare만 열고 send adapter는 runtime unavailable로 유지한다.

## 결정: Desktop process-memory snapshot

Attachment snapshot은 Desktop process memory에만 존재한다. Sidecar/Cline, Application/Domain, SQLite/WAL/backup, Workflow ledger, audit와 diagnostic bundle은 snapshot store를 소유하지 않는다.

- snapshot ID는 conversation, workflow run, capability version, prepare step과 local execution context에 묶인 unguessable opaque identity다.
- 한 workflow run에는 active snapshot 1개, process 전체에는 active snapshot 2개와 aggregate raw byte 32 MiB만 허용한다.
- 파일은 최대 8개, 개별 raw byte는 8 MiB, snapshot aggregate는 16 MiB다.
- TTL은 trusted clock 기준 5분이다. restart, expiry, cancel, replacement, run completion, terminal send와 dispose/shutdown에서 snapshot을 폐기한다.
- snapshot은 one-use다. approval/ticket 실패 뒤 임의로 재사용하지 않으며 새 선택과 승인을 요구한다.
- path, leaf filename, file identity, content hash와 byte는 필요한 동안 Desktop memory에만 둔다.
- leaf filename은 path component를 제거하고 NFC로 정규화한 뒤 Unicode scalar 255개와 UTF-8 1,024 byte로 제한하며 NUL/CR/LF/control을 거부한다. 같은 snapshot 안의 대소문자 무시 중복 이름도 거부한다.

## 결정: local fixed filesystem만 fail-closed로 허용

system picker가 반환한 항목도 즉시 신뢰하지 않는다. initial slice는 local fixed volume의 NTFS/ReFS regular file만 허용한다.

다음 대상은 모두 거부한다.

- UNC/network share, device path, volume/device namespace와 alternate data stream
- directory, glob, removable/remote volume과 NTFS/ReFS 여부를 확정할 수 없는 filesystem
- 어느 path segment나 최종 파일의 reparse point, symlink, junction 또는 mount traversal
- offline, recall-on-open/data-access와 그 밖의 cloud placeholder
- size, identity, timestamps 또는 stable handle semantics를 확정할 수 없는 대상

Desktop은 canonical path 문자열 비교만으로 판정하지 않는다. handle을 연 뒤 final path, volume/filesystem, file identity, attributes와 regular-file 여부를 확인한다.

## 결정: byte snapshot과 source revalidation

prepare는 각 파일에 대해 다음 순서를 지킨다.

1. no-follow 성격의 handle에서 volume/file identity, leaf filename, attributes, size와 write time을 기록한다.
2. bounded buffer에 exact byte를 읽으며 content hash를 계산한다.
3. 같은 handle의 identity, size, write time과 attributes를 다시 읽어 중간 변경을 거부한다.
4. order, NFC-normalized leaf filename, stable file identity, size와 content hash를 length-framed canonical manifest에 넣고 `attachmentManifestHash`를 계산한다.
5. 성공한 byte buffer를 immutable snapshot으로 봉인한다.

승인 직전과 network effect 직전에는 source를 다시 열어 volume/file identity, reparse/cloud attributes, size와 content hash를 재검증한다. 어느 하나라도 달라지면 기존 snapshot을 폐기·zeroize하고 재선택과 재승인을 요구한다. 승인 후 전송은 경로를 다시 따라가지 않고 승인된 immutable snapshot byte만 사용한다.

hard link 자체를 이름만으로 탐지할 수 있다고 주장하지 않는다. stable file identity와 exact content revalidation이 승인한 byte를 묶고, path가 바뀌거나 link 대상 content가 변하면 fail closed한다.

## 결정: 이름·byte 노출과 zeroization

파일 이름과 크기는 picker/approval UI 및 승인된 SMTP MIME에만 나타난다. path는 picker가 제공하는 Desktop UI context 밖으로 내보내지 않는다. Tool result, error, activity summary, Workflow ledger, audit, log, SQLite/WAL/backup, Sidecar IPC와 diagnostic bundle에는 path, filename, identity, content hash와 byte를 기록하지 않는다.

- raw byte와 encoder scratch buffer는 가능한 한 zeroizable mutable memory로 보관하고 immutable string/base64 전체 copy를 만들지 않는다.
- reject, expiry, cancel, replacement, completion, failure, `outcome_unknown`, dispose와 shutdown의 모든 terminal path에서 `CryptographicOperations.ZeroMemory`에 해당하는 best-effort zeroization을 수행한다.
- managed runtime과 OS/network stack의 모든 내부 copy가 물리적으로 지워진다고 주장하지 않는다. 구현은 소유 buffer의 수명과 copy 수를 검증 가능한 범위로 최소화한다.
- failure/result text에는 fixed local code만 쓰고 path, filename과 parser exception 원문을 포함하지 않는다.

## 결정: D-6C4 journal identity와 deterministic MIME

D-6C4는 attachment operation의 replay identity와 trigger를 구현 시점의 next available schema에서 명시적으로 version한다. schema 21은 이후 범용 Builder semantic fixture 저장 경계가 사용했으므로 이 동결 계획은 schema 번호도 예약하지 않는다.

- journal은 exact `mail.send_with_attachments.v1 / R3 / Never / Effect`, draft hash, local binding hash, attachment manifest hash/count/total byte와 tool-call identity만 저장한다.
- text-only row의 attachment metadata는 모두 null이고 attachment row는 모두 exact non-null이어야 한다.
- path, filename, file identity, per-file content hash, MIME와 attachment byte column은 금지한다.
- 같은 tool-call replay는 draft, binding과 attachment manifest metadata가 모두 같을 때만 같은 operation으로 해석한다.

SMTP MIME는 deterministic하게 생성한다.

- attachment Content-Type은 initial slice에서 `application/octet-stream`으로 고정하고 filename은 안전하게 encode한다.
- Bcc는 envelope에만 존재한다. header injection을 차단하고 CRLF, base64 line wrapping, dot-stuffing과 SMTP line limit을 wire fixture로 고정한다.
- full-message string이나 full base64 copy를 만들지 않고 bounded buffer로 stream한다.
- encoded MIME 전체 hard cap은 24 MiB다. EHLO가 `SIZE`를 광고하면 advertised limit과 local cap 중 작은 값을 적용하고 envelope/DATA 전에 거부한다.
- partial RCPT reject와 DATA ambiguity는 ADR 0043의 no-retry/no-reconcile 상태 의미를 그대로 사용한다.

## 단계별 인수 조건

### D-6C3

- 구현 시점 신규 protocol의 양쪽 generated contract parity와 historical Protocol 1.23/1.24 fixture가 통과해야 한다.
- 두 계약에서 additional property와 모든 path/filename/byte field가 거부돼야 한다.
- fixed NTFS/ReFS, UNC/device/ADS/reparse/cloud placeholder policy를 deterministic fixture로 검증해야 한다.
- count/size/process/TTL/concurrent prepare bounds와 모든 terminal path zeroization을 검증해야 한다.
- path/name/byte canary가 Tool result, ledger, audit, SQLite/WAL/backup, diagnostic과 Sidecar IPC에서 0건이어야 한다.
- picker/preview keyboard, screen-reader와 760×500 compact UIA를 통과해야 한다.
- send adapter는 unavailable이고 network/journal effect가 0회여야 한다.

### D-6C4

- schema 20→21 migration/reopen/recovery/trigger와 release manifest fixture가 통과해야 한다.
- approval 전 source identity/hash mismatch와 manifest/binding/tool-call mismatch가 network 0회여야 한다.
- MIME wire의 recipients/Bcc/header/base64/dot-stuff/line/size가 exact fixture와 일치해야 한다.
- approval UI와 SMTP wire 외에 attachment path/name/byte canary가 0건이어야 한다.
- success/failure/unknown/cancel/dispose/restart의 snapshot과 transient buffer zeroization을 검증해야 한다.
- 실제 Workflow → picker → prepare → full approval → SMTP queued 수직 회귀, UI smoke와 전체 verify를 통과한 뒤에만 runtime available로 전환한다.

## 의도적으로 미지원

- 기존 mail v1 계약에 attachment/path field를 추가하는 방식
- Mail Gateway v1 attachment, HTML, inline/CID, arbitrary media-type inference와 arbitrary header
- UNC/network/removable/non-NTFS/ReFS, reparse, ADS, directory, glob와 cloud placeholder
- disk/temp spool, SQLite blob, restart-resumable 또는 offline attachment queue
- archive 자동 생성/해제, malware scan 또는 DLP를 제공한다고 주장하는 기능
- attachment snapshot의 persistent/conversation grant 또는 다른 run에서 재사용

## 결과

- 모델은 파일 경로를 만들거나 관찰하지 않고 사용자가 고른 bounded byte만 effect에 결합된다.
- 기존 no-send draft와 text-only send 계약은 호환성을 유지한다.
- approval identity는 초안, connection과 실제 attachment byte manifest를 함께 묶는다.
- 파일 변경과 TOCTOU는 source revalidation과 immutable snapshot으로 fail closed한다.
- 민감한 파일 데이터의 durable/IPC 노출 범위가 SMTP wire 외에는 Desktop memory로 제한된다.

## 대안

- 기존 `mail.send.v1`에 path 배열을 추가하는 방식은 모델 path selection과 계약 호환성 문제 때문에 채택하지 않는다.
- 승인 후 경로에서 다시 읽는 방식은 승인 byte와 전송 byte가 달라질 수 있어 채택하지 않는다.
- temp file이나 SQLite blob에 spool하는 방식은 restart recovery보다 content-at-rest 최소화를 우선하므로 initial slice에서 채택하지 않는다.
- snapshot byte를 Sidecar에 전달해 MIME를 만드는 방식은 replaceable runtime에 파일 content를 노출하므로 채택하지 않는다.
