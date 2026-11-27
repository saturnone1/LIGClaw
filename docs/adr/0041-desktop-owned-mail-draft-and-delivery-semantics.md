# ADR 0041: Desktop-owned mail draft와 delivery semantics

> 상태: 채택 · D-6A/D-6B 인수 완료 · D-6C 설계 승인/미구현
> 기준일: 2026-08-02
> 관련 계획: `../D_STAGE_PLAN.md`
> D-6C 계획: `../D6C_PLAN.md`
> 인수 증거: `../D6A_ACCEPTANCE.md`, `../D6B_ACCEPTANCE.md`
> 후속 결정: `0042-fixed-mail-gateway-and-durable-submission-journal.md`, `0043-typed-smtp-fallback-and-data-outcome-ambiguity.md`, `0044-desktop-owned-attachment-snapshots-and-mail-contracts.md`

## 적용 범위와 상태

이 ADR은 인수 완료한 D-6A/D-6B와 설계만 승인된 D-6C의 안전 가드를 명시적으로 구분한다.

| 단계 | 이 ADR에서의 상태 | 범위 |
|---|---|---|
| D-6A | 인수 완료 | Protocol 1.22의 text-only, no-send `mail.create_draft.v1` |
| D-6B | 인수 완료 | Protocol 1.23의 고정 Mail Gateway, metadata-only durable submission journal, idempotency와 delivery 상태 |
| D-6C | 설계 승인, 미구현 | schema 19/20/21, typed SMTP와 Protocol 1.24 attachment snapshot/send |

D-6B는 Protocol 1.23과 schema 18 기준으로 전체 인수를 마쳤다. D-6C 문서의 schema/protocol 번호와 gate는 후속 구현이 넘어서는 안 되는 설계 경계이며 SMTP transport, attachment snapshot/send 또는 관련 UI가 현재 구현됐다는 뜻은 아니다. 각 Protocol hash와 schema 번호는 해당 단계의 인수 스냅샷으로 관리한다.

## 맥락

Feature Builder와 Workflow가 폐쇄망 업무를 조합하려면 메일 콘텐츠를 안전하게 준비할 수 있어야 한다. 그러나 초안 준비와 외부 전송을 같은 Tool로 합치면 단순한 문안 생성이 endpoint, sender, credential, retry와 실제 외부 효과를 암묵적으로 소유하게 된다. 특히 임의 HTTP 요청이나 SMTP 호출로 메일 전송을 우회하면 Desktop의 승인, secret, 저장소와 감사 경계를 보장할 수 없다.

따라서 첫 수직 slice는 전송 기능이 아니라, 입력을 엄격히 검증하고 콘텐츠를 노출하지 않는 결정적 메타데이터만 반환하는 Workflow 내부 기능으로 제한한다. 실제 delivery는 별도의 effect 계약과 durable 상태 머신을 갖춘 후속 단계로 남긴다.

## 결정: 현재 구현된 D-6A

### Protocol 1.22의 text-only, no-send 계약

- canonical Tool 이름은 `mail.create_draft.v1`이다.
- 입력은 `to`, 선택적 `cc`/`bcc`, `subject`, `body`만 허용하며 `additionalProperties: false`다. 본문은 plain text로만 취급한다.
- `sender`/`from`, `endpoint`/`url`, `connectionSlot`, token/auth/secret, 임의 header, HTML, attachment와 전송 사유는 입력과 결과에 존재하지 않는다.
- 결과는 `ready`, `draftHash`, `recipientCount`, `subjectLength`, `bodyLength`만 포함한다. `ready: true`는 현재 입력이 D-6A 정책을 통과했다는 뜻일 뿐, 초안 저장·발송 대기·Gateway 접수·delivery 완료를 뜻하지 않는다.
- Protocol 1.22의 generated C#/TypeScript 계약 hash는 `a107ca46484eebc707c568514187c9896275adb51d152cb0490a847eafc415b7`이다.
- Workflow schema subset은 문자열 배열의 `uniqueItems`를 양쪽 validator에서 동일하게 지원한다. 일반 정규식 `pattern`을 실행 경계에 열지 않고 result 계약은 hash 길이를 71자로 제한하며, producer가 exact `sha256:` 소문자 hex 형식을 강제한다.

### transport-independent Application 정책

- `MailDraftPolicy`는 WPF, SQLite, Sidecar, Cline, MCP, HTTP, SMTP, Windows adapter와 credential 저장소를 참조하지 않는다.
- 전체 수신자는 최대 16명이며 `to`는 최소 한 명이어야 한다. 주소는 bounded ASCII mailbox 형식으로 검사하고 `to`/`cc`/`bcc` 전체에서 대소문자를 무시한 중복을 거부한다.
- 제목은 최대 200 Unicode scalar와 800 UTF-8 byte, 본문은 최대 8,192 scalar와 24,576 UTF-8 byte로 제한한다. 제목의 줄바꿈·제어 문자를 거부하고 본문은 tab과 LF만 허용한다.
- 본문의 CRLF/CR은 LF로 정규화한다. 수신자 category와 순서, 각 field의 길이 경계를 포함한 canonical frame으로 `sha256:` draft hash를 계산한다.
- 정책 결과와 오류 코드는 외부 transport 상태를 표현하지 않으며 fixed error code만 사용한다.

### Desktop Workflow 전용 실행 경계

- 실행 adapter는 Desktop의 `MailCreateDraftTool`이며 `IWorkflowOnlyObservationToolAdapter`로 등록한다.
- admission metadata는 `R0 / None / ReadOnly / Observation`이다. 일반 `tool.invoke` 경로는 이 adapter를 거부하고 Desktop Workflow executor 내부에서만 실행할 수 있다.
- 실행 중 네트워크, 파일, SQLite, Credential Manager, Sidecar RPC와 Windows 외부 효과를 호출하지 않으며 초안 원문을 durable store에 저장하지 않는다.
- 성공 결과는 hash와 count/length만, 실패 결과는 fixed code와 고정 활동 요약만 내보낸다. 수신자, 제목과 본문 원문은 Tool 결과, 오류, `ToString()`과 로그에 기록하지 않는다.
- D-6A는 발송 승인을 요청하거나 prepared one-shot effect를 만들지 않는다. 외부 상태를 바꾸지 않기 때문에 위의 observation metadata를 사용한다.

## 결정: 인수 완료한 D-6B Mail Gateway

D-6B는 다음 조건을 만족하는 별도 effect slice로 인수됐다.

- 전송 대상은 관리자가 구성한 **고정 Mail Gateway** adapter로 한정한다. Workflow나 모델이 endpoint, HTTP method, URL mapping, header 또는 generic POST를 제공할 수 없다.
- endpoint, sender identity와 secret은 Desktop-owned Connection/Credential 경계에서만 입력·보관·해결한다. Feature Builder 질문, Workflow payload, Sidecar와 로그로 전달하지 않는다.
- 전송은 D-6A와 별개의 R3 prepared one-shot effect 및 AllowOnce 승인 경계를 통과해야 한다. 승인 ticket은 exact draft hash, connection binding과 전송 의도를 묶어야 한다.
- 네트워크 호출 전에 durable outbox row를 원자적으로 기록하고 stable operation ID와 idempotency key를 부여한다. 재시작과 timeout 뒤에도 동일 논리 전송이 중복 enqueue되지 않아야 한다.
- `queued`는 Gateway가 요청을 접수했다는 뜻일 뿐 `delivered`가 아니다. delivery는 Gateway의 별도이고 신뢰 가능한 최종 증거가 있을 때만 기록한다. 접수 응답만으로 delivered를 반환하거나 UI에 표시하지 않는다.
- 자동 retry는 Gateway가 같은 idempotency key의 중복 억제를 보장하고 결과를 조회·조정할 수 있을 때만 허용한다. 결과가 불명확하면 새 전송을 만들지 않고 reconciliation 상태로 남긴다.

Protocol 1.23 `mail.send.v1`, exact `mail-api` Connection, schema 18 submission journal/reconciliation과 prepared-send UI의 세부 인수 증거는 ADR 0042와 `D6B_ACCEPTANCE.md`에 고정돼 있다.

## 결정: 설계 승인·미구현 D-6C SMTP와 attachment

- D-6C0는 D-6B unresolved 조회의 partial-index/query-plan 성능 문제만 schema 19에서 닫는다. Protocol과 실행 의미는 바꾸지 않는다.
- D-6C1/2는 schema 20의 별도 typed `smtp` Connection과 기존 Protocol 1.23 `mail.send.v1`의 text-only SMTP 실행을 추가한다. `fallback`은 관리자의 명시적 connection-kind 선택이며 Gateway 실패 시 자동 전환이 아니다.
- SMTP `DATA` 이후 연결 단절처럼 서버가 메시지를 수락했는지 알 수 없는 결과는 `outcome_unknown`으로 취급한다. stable Message-ID는 진단용일 뿐이며 automatic retry/reconcile/fallback과 exactly-once 주장은 없다.
- D-6C3/4는 기존 mail v1에 path field를 덧붙이지 않고 Protocol 1.24의 `mail.prepare_selected_attachments.v1`과 `mail.send_with_attachments.v1`을 별도로 추가한다.
- attachment path/name/byte는 bounded immutable snapshot으로 Desktop process memory에만 두고, local fixed NTFS/ReFS, no-reparse/UNC/ADS 정책과 source revalidation/zeroization을 적용한다. 승인은 exact attachment manifest와 draft/delivery identity를 함께 묶는다.
- HTML body와 임의 header가 필요해져도 D-6A plain-text 계약을 확장해 우회하지 않는다. 별도의 schema, sanitizer와 policy 검토가 선행돼야 한다.

구현 순서, 단계별 acceptance gate와 unsupported 범위는 `D6C_PLAN.md`, SMTP 상태 의미는 ADR 0043, attachment 계약/저장 경계는 ADR 0044를 따른다. 이 항목들은 현재 SMTP transport, attachment snapshot/send 또는 관련 UI가 구현됐다는 주장이 아니다.

## 결과

- Feature Builder와 Workflow는 발송 권한 없이 bounded plain-text 메일 내용을 검증하고 결정적 identity를 만들 수 있다.
- `mail.create_draft.v1` 성공은 저장이나 전송 상태로 오해되지 않으며, 향후 delivery의 외부 효과와 분리된다.
- Application 정책은 transport와 Desktop infrastructure로부터 독립적이어서 Gateway와 SMTP 선택이 바뀌어도 재사용할 수 있다.
- D-6B Gateway 전송은 Desktop 승인, secret, metadata-only durable submission journal과 idempotency를 사용하며 접수와 delivery 상태를 구분한다.
- D-6C의 attachment와 SMTP 불명확 결과는 기존 v1 계약에 섞이지 않고 별도 위험 모델, 계약과 staged acceptance를 요구한다.

## 대안

- D-6A가 SMTP 또는 HTTP를 직접 호출하는 방식은 no-send 의미와 Desktop effect 경계를 깨므로 채택하지 않는다.
- Cline/Sidecar가 메일 transport, credential 또는 retry를 소유하는 방식은 replaceable runtime에 외부 효과를 부여하므로 채택하지 않는다.
- 기존 `connection.http.request.v1`에 endpoint/header/body를 조합해 generic POST로 보내는 방식은 고정 Gateway와 typed payload 정책을 우회하므로 채택하지 않는다.
- 메모리에서 검증한 초안만으로 `queued` 또는 `delivered`를 반환하는 방식은 durable evidence가 없으므로 채택하지 않는다.
- SMTP timeout을 성공 또는 일반 실패로 단정하고 자동 재전송하는 방식은 중복 메일 위험 때문에 채택하지 않는다.
- attachment 경로를 현재 draft schema에 문자열로 추가하는 방식은 파일 identity, TOCTOU, 크기와 데이터 유출 경계를 보장하지 못하므로 채택하지 않는다.
