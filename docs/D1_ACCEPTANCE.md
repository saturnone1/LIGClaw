# D-1 Capability Catalog acceptance

> 상태: 완료
> 기준일: 2026-08-02
> 관련 계획: `D_STAGE_PLAN.md`
> 아키텍처 결정: `adr/0036-retain-cline-sidecar-and-desktop-owned-capability-control-plane.md`

## 결과

D-1은 Cline Node.js Sidecar와 protocol 1.17을 변경하지 않고 Desktop 소유 Capability Catalog를 추가했다. 지원 범위는 executable을 포함하지 않는 `prompt-v1` `.ligcap` package다. Capability 활성화는 Tool 실행 권한이 아니며, D-2 이후 실제 효과도 기존 Desktop 정책·승인·감사 경로를 통과해야 한다.

구현된 사용자 흐름은 다음과 같다.

```text
.ligcap 선택
 → bounded in-memory 검사
 → ready_for_review draft
 → exact id/version/content-hash 검토
 → catalog revision CAS 활성화
 → active-only FTS 후보
 → immutable version export 또는 activation history 기반 rollback
```

## 완료 조건 증거

| 완료 조건 | 구현 및 결정적 증거 |
|---|---|
| prompt-only package import·검토·활성화·rollback | `CapabilityManagementServiceTests.RealCatalogCompletesImportReviewActivateRollbackAndExport`가 실제 ZIP codec, Application service, SQLite repository를 함께 사용해 v1/v2 활성화와 v1 rollback·export를 검증한다. |
| 재시작 후 active exact version 복원 | `CapabilityCatalogRepositoryTests.RestoresExactActiveVersionAndHashAfterRestart` |
| 손상·중복·path traversal·schema mismatch 거부 | `ZipLigcapPackageCodecTests`의 truncated archive, traversal/root/backslash, duplicate/case collision, unsupported schema version, bounded JSON Schema 회귀와 실제 management duplicate import 회귀 |
| secret이 package·SQLite·로그에 남지 않음 | known token/credential, 민감 property의 direct·nested `const`/`enum`, placeholder suffix 우회를 거부한다. 오류에는 원문을 싣지 않고 `SensitiveSchemaLiteralIsRejectedBeforeAnyCapabilityRowIsStored`가 네 Capability table 0행을 확인한다. 새 관리 경로에는 원문 logging 호출이 없고 기존 diagnostic redactor 회귀를 유지한다. |
| active 후보만 최대 8개 | schema 13 `capability_search`는 active exact version의 display name, description, intent examples만 색인한다. inactive draft와 instructions/schema/source는 색인하지 않으며 activation·rollback·restart·deterministic max-8 회귀가 있다. |
| 최소 WPF 관리 UI | `CapabilitiesPage`, `CapabilityReviewWindow`, MainWindow AppRail, UI construction/XAML 회귀와 실제 760×500 UIA smoke가 import, filter, sort, list, keyboard navigation을 확인한다. |

## 불변성과 승인 결속

- `capability_versions`는 insert-only이고 `(capability_id, version)`은 하나의 content hash만 허용한다.
- draft의 identity, payload, source, parent와 expected base는 trigger로 변경·삭제할 수 없다. lifecycle status, revision과 timestamp만 CAS로 갱신한다.
- 승인 명령은 검토한 exact `CapabilityVersionRef`와 draft/catalog revision, expected active version을 함께 전달한다.
- activation transaction은 저장 payload의 SHA-256을 다시 계산하고 exact ref와 다르면 version, active pointer, activation ledger, draft state를 모두 변경하지 않는다.
- rollback은 version을 수정하지 않고 과거 exact version을 가리키는 새 activation ledger 행과 증가한 catalog revision을 만든다.
- activation과 rollback 직전에 현재 Windows profile에서 실제 사용 가능한 Tool inventory와 canonical risk를 다시 검사한다.

## package와 secret 경계

- ZIP을 디스크에 추출하지 않는다. raw archive 1 MiB, expanded 512 KiB, entry 4개와 entry별 상한을 적용한다.
- 허용 entry는 `manifest.json`, `instructions.md`, `input.schema.json`, 선택적 `settings.schema.json`뿐이다.
- traversal, absolute/root path, backslash, duplicate/case collision, symlink/reparse/non-regular entry, nested archive/executable magic을 거부한다.
- strict UTF-8, BOM·control·Unicode format 문자와 단일행 label의 tab/line separator를 거부한다.
- JSON duplicate/case collision, depth/token과 bounded schema subset을 검사한다.
- `password`, `apiKey`, `clientSecret`, `token`, `authorization`, private/signing key 계열의 민감 문맥은 nested object/array까지 전파한다. literal은 정확한 fixture/redacted placeholder만 허용한다.
- archive byte hash와 canonical logical content hash를 분리하며 export는 고정 entry 순서·timestamp·LF로 결정적이다.

## 저장과 UI

- SQLite current schema는 13이다. `capabilities`, `capability_versions`, `capability_drafts`, `capability_activations`, FTS5 `capability_search`를 Desktop이 소유한다.
- package 원문, source path, credential, secret column을 저장하지 않는다.
- WPF는 `ICapabilityManagementService` 하나만 사용한다. SQLite, ZIP, Sidecar, Cline, Credential Manager에 직접 의존하지 않는다.
- import/export path는 `.ligcap`, 크기, 기존 parent reparse를 검사한다. export는 sibling temporary file을 거쳐 교체하며 실패 시 기존 목적지를 보존한다.

## 검증 결과

저장소 루트의 `./scripts/verify.ps1`과 `./scripts/smoke-ui.ps1`을 통과했다. 최종 수치는 마지막 검증 실행 결과로 갱신한다.

- Sidecar: 84
- Contracts: 7
- Application: 62
- Desktop: 468
- Named Pipe integration: 1
- build: warning 0, error 0
- UIA smoke: integrated pages 6, 760×500, application error 0

`npm audit`의 기존 `@ai-sdk/provider-utils` low advisory 1건은 Cline transitive dependency로 추적하며 자동 major/transitive 변경을 적용하지 않았다.

## 다음 단계

D-2는 schema-first `CapabilityRuntimeSnapshot`과 run별 frozen authorization을 추가한다. 현재 run은 시작 시 고정한 candidate exact refs만 사용하고 catalog 변경은 다음 run/session에만 반영한다. Sidecar는 Cline projection을 소유하지만 후보 선정, stale/version/hash, required Tool과 canonical risk의 최종 검증은 Desktop이 독립적으로 수행한다.
