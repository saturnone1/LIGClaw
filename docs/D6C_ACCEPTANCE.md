# D-6C0~2 text-only SMTP reference acceptance

> 상태: text-only reference 구현 완료·동결 · D-7 범용 진입/cross-layer integration 및 Protocol 1.24/schema 21 전체 verify/UI smoke 인수 완료
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`, `D6C_PLAN.md`
> 아키텍처 결정: `adr/0043-typed-smtp-fallback-and-data-outcome-ambiguity.md`
> D-6C 구현 스냅샷: Protocol 1.23 · contract hash `4230d92e94aa21e6dd9f166ed8ed7c30b8a9827c3ebd93ef8f787b848297ace1` · SQLite schema 20
> 현재 global 기준: Protocol 1.24 · contract hash `7a221a94e96ef061630a92382356761cea49acdf271d0503a915c31ccd013776` · SQLite schema 21

## 현재 판정

D-6C0의 unresolved journal 조회 preflight, D-6C1의 typed SMTP Connection과 durable phase, D-6C2의 text-only SMTP 실행 수직을 구현했다. SMTP는 Mail Gateway 실패 시 자동 fallback이 아니라 Capability version에 결합된 별도 exact connection kind다. D-6C는 당시 Protocol 1.23의 `mail.send.v1` 계약을 넓히지 않았고 Sidecar/Cline은 SMTP socket, credential, MIME, journal 또는 재시도 판단을 소유하지 않는다.

이메일은 LIGClaw의 최종 기능 범위가 아니라 LLM에 새 기능을 추가하는 공통 Capability 경로의 기준 구현이다. text-only SMTP에서 mail-specific 구현을 동결했다. 이후 Protocol 1.24 범용 Builder 진입, exact review focus와 비메일 activation/next-run cross-layer integration을 구현했으며 상세 증거는 `D7_ACCEPTANCE.md`를 따른다. 이 비메일 integration은 fake Builder runtime과 실서비스/SQLite/orchestrator를 잇는 결정적 회귀이고 실제 UI/vLLM/Named Pipe 단일 E2E는 아니다.

현재 global Protocol/schema 상승은 D-6C mail 계약 확장이 아니다. Protocol 1.24는 `capability.builder.open.v1`, schema 21은 Observation-only semantic fixture v2 저장 경계가 소유한다.

## 구현 범위

- [x] schema 19의 `submitting | outcome_unknown` partial composite index와 production query-plan 고정
- [x] schema 20 migration, 기존 Mail Gateway row의 `mail-api` backfill과 typed SMTP profile/probe/binding/outbox metadata
- [x] SMTP 전용 Credential Manager target과 `AUTH PLAIN` credential codec/zeroization
- [x] system trust·hostname 검증의 implicit TLS 또는 required STARTTLS, post-TLS EHLO와 bounded reply parser
- [x] probe의 인증-only 경계와 `MAIL FROM`/`RCPT TO`/`DATA` 0회
- [x] Workflow-only R3/AllowOnce/one-shot `mail.send.v1` SMTP prepared effect
- [x] 모든 RCPT 수락 뒤 DATA, server `354` 뒤 첫 message byte 전 durable `data_started` CAS
- [x] deterministic UTF-8 text/plain MIME, Bcc envelope-only, CRLF/base64/dot-stuffing과 stable Message-ID
- [x] final positive reply는 `queued`만, DATA 이후 불명확 결과는 `outcome_unknown`과 attention-required
- [x] outcome unknown 뒤 reconnect/retry/reconcile/transport fallback/새 outbox 0회
- [x] HTTP/Mail Gateway/SMTP mutation과 final resolve가 한 production `ConnectionExecutionLeaseCoordinator` 공유
- [x] Connections와 Capability review의 typed SMTP save/probe/rebind/readiness UI
- [x] Protocol 1.24/schema 21을 포함한 현재 통합 작업 트리 전체 `./scripts/verify.ps1` — 102.5초 exit 0; Sidecar 141/141, Contracts 24/24, Application 454/454, Sidecar Integration 1/1, Desktop 847/847, warning/error 0
- [x] 현재 통합 작업 트리 `./scripts/smoke-ui.ps1 Debug` — 28.9초 exit 0; Builder handoff, IntegratedPages 8, 760×500, InputFocusRestored passed, ApplicationErrors 0

## 현재 표적 증거

- Application SMTP policy/static validator selected regression: 70/70
- SMTP persistence, credential, resolver와 management selected regression: 30/30
- SMTP formatter, wire와 prepared send vertical selected regression: 31/31
- generic Workflow ledger/unknown mapping selected regression: 13/13
- UI construction/XAML composition selected regression: 27/27
- Desktop production build: warning 0, error 0

표적 wire 회귀는 1-byte fragmentation, reply bound, TLS trust/hostname/expired certificate fail-closed, STARTTLS downgrade, AUTH advertisement, partial RCPT, cancellation, DATA durable callback의 미기록/기록/불확정 상태와 dispose failure를 포함한다. loopback TLS negative fixture는 application command 0회를 확인하며 in-memory/loopback 인증서만 사용하고 Windows trust store를 변경하지 않는다.

Sidecar projection/bridge 회귀는 bounded structured Workflow input이 current-user IPC로 Desktop에 전달되는 사실을 확인한다. Sidecar의 해당 경로는 process-memory session/pending map과 fixed 오류만 사용하고 파일/DB 진단 write 경로가 없다는 코드 감사를 통과했지만, mail canary 기반 no-log end-to-end라고 과장하지 않는다. 이 D-6C focused evidence를 현재 전체 verify 결과로 합산하지 않는다.

## 반드시 보존할 의미

- `smtp`와 `mail-api` 중 exact Capability binding 하나만 실행한다. 둘 다 맞거나 둘 다 맞지 않으면 network 전에 거부하며 runtime fallback은 없다.
- transport, host, port, TLS mode, auth, sender와 Message-ID는 Workflow/model input이 아니다.
- approval 전 binding drift, journal stage 실패와 `data_started` durable commit 실패는 message byte 0회다.
- `data_started` commit-then-throw는 durable row를 query-back해 기록 여부를 확인하고 기록되지 않았으면 전송하지 않는다.
- DATA 이후 final reply가 없거나 malformed/timeout/cancel/disconnect이면 실제 접수 여부를 숨기지 않고 `outcome_unknown`으로 남긴다.
- 같은 draft hash의 unresolved SMTP operation은 binding이 달라도 다음 승인에서 새 접속·전송을 만들지 않고 같은 stable Message-ID로 `ReviewOutcome`을 요구한다.
- SMTP `queued`는 서버 접수이지 최종 배달이 아니다. 이 adapter는 `delivered`를 만들지 않는다.
- 실행 invocation의 recipient/subject/body는 승인 UI와 SMTP wire 외에 result, outbox, Workflow ledger, activity/audit와 diagnostic에 추가 복제하지 않는다. username/password는 Windows Credential Manager에만 저장한다.
- 사용자가 채팅에 직접 입력한 문장은 기존 conversation/FTS 정책에 따라 영속되고 모델 context로 전달되며, schema-bound Workflow input은 Sidecar→Desktop IPC를 통과한다. 사용자가 검토·승인한 immutable Capability 정의의 literal mail field도 Catalog에 영속될 수 있다. Sidecar는 원문을 durable storage나 diagnostic log에 남기지 않지만, 이 인수는 입력 원본 전체가 SQLite/transcript/model/IPC에서 0건이라고 주장하지 않는다.

## 현재 제외 범위

- attachment, HTML, arbitrary header, reply-to, SMTPUTF8, DSN과 delivery/read receipt
- plaintext/opportunistic TLS, certificate bypass와 `AUTH PLAIN` 외 인증
- outcome unknown의 자동 resend/reconcile 또는 사용자 강제 terminal 처리
- Mail Gateway↔SMTP runtime fallback
- D-6C3/4 attachment 계약과 구현 시점의 next available schema

현재 통합 작업 트리의 전체 verify와 UI smoke가 통과해 D-6C0~2 최종 저장소 gate를 닫았다. D-6C3/4 attachment는 범용 플랫폼 완료 조건이 아니라 동결된 후속 계획이며, 실제 별도 요구와 새 contract/schema 결정 전까지 구현하지 않는다.
