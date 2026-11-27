# D-6A text-only mail draft acceptance

> 상태: 인수 완료
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0041-desktop-owned-mail-draft-and-delivery-semantics.md`
> D-6A 인수 스냅샷: Protocol 1.22 · contract hash `a107ca46484eebc707c568514187c9896275adb51d152cb0490a847eafc415b7` · SQLite schema 16 변경 없음

## 현재 판정

D-6A는 합쳐진 작업 트리에서 계약 생성 parity, Application/Workflow schema 정책, Desktop 수직 회귀와 `./scripts/verify.ps1`를 모두 통과했다. 전송 효과가 없는 text-only draft slice만 인수 완료하며 D-6B Mail Gateway/outbox와 D-6C attachment/SMTP는 완료로 간주하지 않는다.

현재 slice는 `mail.create_draft.v1`의 bounded plain-text 입력을 Desktop Workflow 내부에서 검증하고 콘텐츠 없는 메타데이터를 반환한다. 메일을 저장하거나 보내지 않으며 Mail Gateway, SMTP, sender, endpoint, secret, header, HTML과 attachment를 구현하지 않는다.

## 구현 표적과 결정적 증거

| 표적 | 현재 구현 | 검증 상태 |
|---|---|---|
| schema-first 계약 | `contracts/tools/mail.create_draft.v1.schema.json`과 result schema는 입력을 `to/cc/bcc/subject/body`, 결과를 `ready/draftHash/recipientCount/subjectLength/bodyLength`로 닫는다. generated C#/TypeScript는 Protocol 1.22와 동일 contract hash를 가진다. | Contracts 20/20, Sidecar registry 3/3, generator parity 통과 |
| transport-independent 정책 | `LIGClaw.Application.Mail.MailDraftPolicy`는 recipient/문자/UTF-8 byte/control bounds, LF 정규화, category·순서·length-framed SHA-256를 담당한다. transport, storage, UI와 secret dependency가 없다. | `MailDraftPolicyTests` 33/33 통과 |
| schema subset parity | 문자열 배열의 `uniqueItems`를 Application runtime/static validator, package admission과 Sidecar projection에서 같은 의미로 지원한다. 일반 regex `pattern`은 Workflow subset에 열지 않고 result hash는 길이 71로 제한한다. | Application 관련 묶음 68/68, Sidecar projection 15/15 통과 |
| Desktop Workflow-only 경계 | `MailCreateDraftTool`은 `IWorkflowOnlyObservationToolAdapter`이며 metadata catalog는 `R0 / None / ReadOnly / Observation`으로 노출한다. 일반 Tool 실행 경로는 거부한다. | Mail Tool·metadata·수직 묶음 7/7 통과 |
| no-send/no-persist | adapter는 검증과 metadata 생성 외에 network, SQLite, Credential Manager, file 또는 Sidecar 호출을 하지 않는다. `ready`는 정책 통과만 뜻한다. | 실제 parser→validator→orchestrator→ledger 수직 test 2/2 통과 |
| raw-content 비노출 | 성공 output은 hash/count/length만, 실패는 fixed code와 고정 summary만 가진다. draft/input/validation `ToString()`은 수신자·제목·본문을 echo하지 않는다. | 결과·run·step ledger canary 비노출 회귀 통과 |
| 금지 field fail-closed | sender/from, endpoint/url, connection/secret/auth/token, header, HTML, attachment, reason과 그 밖의 추가 property는 계약과 adapter에서 거부한다. | Contracts 및 Desktop negative test 통과 |

## 인수 검사 항목

### 계약과 생성 parity

- [x] Protocol version이 C#과 TypeScript 모두 `1.22`인지 확인했다.
- [x] generated contract hash가 양쪽 모두 `a107ca46484eebc707c568514187c9896275adb51d152cb0490a847eafc415b7`인지 확인했다.
- [x] `mail.create_draft.v1` input/result의 exact property, required field, `additionalProperties: false`, bounds와 71자 hash shape를 확인했다. exact SHA-256 형식은 producer 회귀가 강제한다.
- [x] `npm run generate --prefix sidecar` 이후 generated output에 미반영 diff가 없음을 확인했다.

### Application 정책

- [x] `to` 필수와 전체 recipient 16명 상한을 확인했다.
- [x] 254자 ASCII address 경계, malformed address와 category 간 case-insensitive 중복 거부를 확인했다.
- [x] 제목 200 scalar/800 UTF-8 byte와 본문 8,192 scalar/24,576 UTF-8 byte의 exact 경계를 확인했다.
- [x] invalid Unicode, 제목 control/newline, 본문의 tab/LF 외 control 거부를 확인했다.
- [x] CRLF/CR의 LF 정규화와 canonical hash 결정성, recipient category·순서·field boundary binding을 확인했다.
- [x] input과 validated collection의 defensive copy, fixed error code와 redacted `ToString()`을 확인했다.

### Desktop 실행 경계

- [x] metadata가 exact `R0 / None / ReadOnly / Observation`이고 Workflow-only marker 없는 동명 adapter는 unavailable인지 확인했다.
- [x] 일반 `tool.invoke`는 거부되고 Workflow executor 경로만 성공하는지 확인했다.
- [x] 성공 결과가 `ready`, `draftHash`, 세 개 length/count field만 포함하고 원문은 포함하지 않는지 확인했다.
- [x] extra transport/sender/attachment field와 malformed input이 fixed code로 fail-closed 하는지 확인했다.
- [x] 성공·실패 결과, activity summary, 예외, workflow/audit 로그에 recipient, subject와 body canary 원문이 남지 않는지 확인했다.
- [x] 실행 중 network, file, SQLite, Credential Manager, Sidecar RPC와 다른 Windows effect가 호출되지 않는지 확인했다.

## 실행 결과

```powershell
dotnet test tests/LIGClaw.Contracts.Tests/LIGClaw.Contracts.Tests.csproj --no-restore --filter "FullyQualifiedName~GeneratedCapabilityContractTests"
dotnet test tests/LIGClaw.Application.Tests/LIGClaw.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~MailDraftPolicyTests|FullyQualifiedName~WorkflowRuntimeContractValidatorTests|FullyQualifiedName~WorkflowStaticValidatorTests|FullyQualifiedName~CapabilityRuntimeInputSchemaPolicyTests"
dotnet test tests/LIGClaw.Desktop.Tests/LIGClaw.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~MailCreateDraftToolTests|FullyQualifiedName~DesktopWorkflowToolMetadataCatalogTests|FullyQualifiedName~MailDraftWorkflowVerticalTests"
./scripts/verify.ps1
```

- 표적: Contracts generated 13/13, Application mail/schema 101/101, Desktop mail 수직 묶음 7/7
- 전체 verify: Sidecar 137/137, Contracts 20/20, Application 296/296, Sidecar Integration 1/1, Desktop 650/650
- .NET build warning/error 0, PowerShell parse, release manifest/evidence와 contract generation parity 통과
- `npm audit`의 기존 transitive low severity advisory 1건은 verify exit 0 상태로 계속 추적한다.

D-6A는 새 WPF 화면을 추가하지 않으므로 UI smoke는 이 slice의 직접 완료 증거가 아니다. 통합 release gate에서 `./scripts/smoke-ui.ps1`를 다시 실행한다면 결과를 별도 기록하며, 실행 전에는 통과로 간주하지 않는다.

## 의도적으로 미지원인 범위

| 미지원 범위 | 후속 단계와 필요한 경계 |
|---|---|
| 실제 메일 enqueue/send/delivery | D-6B의 별도 R3 prepared effect, AllowOnce 승인과 고정 Gateway adapter |
| Mail Gateway endpoint와 sender 설정 | D-6B의 Desktop-owned Connection UI와 typed binding |
| token/password/API key 및 인증 header | D-6B의 Desktop Credential Manager 저장·해결 경계 |
| durable outbox, idempotency, retry와 reconciliation | D-6B의 SQLite migration, operation identity와 상태 머신 |
| `queued`/`delivered` 판정과 delivery 조회 | D-6B의 Gateway receipt와 별도 final-delivery evidence |
| 임의 HTTP mapping/generic POST | 지원하지 않는다. 고정 Mail Gateway 계약으로만 추가한다. |
| SMTP transport | D-6C의 typed TLS/auth adapter. `DATA` 이후 outcome unknown은 자동 retry하지 않는다. |
| attachment | D-6C의 별도 manifest/prepare/spool/approval 계약 |
| HTML body와 임의 header | 별도 schema, sanitizer와 policy 결정 전에는 지원하지 않는다. |
| UIA/webmail 자동화 | D-6A/B/C의 typed transport 경계를 대체하지 않으며 현재 범위가 아니다. |

## 완료 판정

위 조건을 모두 충족했으므로 D-6A를 인수 완료한다. 이후 D-6B 계약이 추가됐더라도 D-6B Desktop effect, repository, 연결, UI와 D-6C는 각 독립 검증이 끝날 때까지 완료로 표시하지 않는다.
