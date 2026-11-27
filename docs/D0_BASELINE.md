# D-0 기준선과 외부 게이트

> 상태: D단계 진입 기준선
> 기준일: 2026-08-02
> 기준 커밋: `b98697b`
> 관련 계획: `D_STAGE_PLAN.md`

## 1. 목적

D단계는 기존 Agent Runtime과 안전 경계를 대체하지 않는다. 이 문서는 Capability 기능을 추가하기 전에 반드시 보존할 provider, Tool, MCP, Agent job, subagent 동작과 자동화할 수 없는 Windows release gate를 분리해 고정한다.

기준선 테스트를 다른 테스트로 교체하거나 삭제할 때는 같은 process boundary와 실패 조건을 검증하는 결정적 테스트를 먼저 추가한다. 단순히 happy path만 남기는 변경은 기준선 충족으로 보지 않는다.

## 2. 자동 회귀 기준선

| 영역 | 고정할 동작 | 주 검증 위치 |
|---|---|---|
| Cline API와 provider | exact `0.0.65` Cline adapter, OpenAI-compatible endpoint, immutable per-run routing, eligible failure에만 bounded fallback, 명시 선택 시 fallback 금지, 민감한 provider 원문 미노출 | `sidecar/test/runtime.test.js`, `sidecar/test/provider-connection-test.test.js` |
| Agent event | run별 0부터 시작하는 연속 sequence, 단 하나의 terminal event, 취소 격리, fallback 시 `routing_changed → run_started → text/terminal` 순서 | `sidecar/test/runtime.test.js`, `tests/LIGClaw.Sidecar.IntegrationTests/SidecarConversationTests.cs` |
| Desktop Tool bridge | schema에 등록된 exact Tool만 노출, Desktop-issued conversation/run/tool-call identity, canonical risk 재검증, R0~R4 승인 경로, timeout·취소·중복 호출 격리 | `sidecar/test/tool-catalog.test.js`, `tests/LIGClaw.Desktop.Tests/Phase1ReplayE2ETests.cs`, `tests/LIGClaw.Desktop.Tests/Phase2ReplayE2ETests.cs` |
| MCP | 최대치와 exact allowlist, connection-scoped auth, 고정 registry stdio, 실패 격리, bounded result, active call 뒤 session retire | `sidecar/test/mcp-client-manager.test.js`, `tests/LIGClaw.Desktop.Tests/McpReadToolTests.cs`, `tests/LIGClaw.Desktop.Tests/McpSettingsSectionControllerTests.cs` |
| Durable Agent job | Desktop SQLite ledger, lease·attempt·misfire·restart reconciliation, 최대 동시 실행 수, bounded result | `tests/LIGClaw.Application.Tests/AgentJobPolicyTests.cs`, `tests/LIGClaw.Desktop.Tests/AgentJobStoreTests.cs`, `tests/LIGClaw.Desktop.Tests/DurableAgentJobSchedulerTests.cs` |
| Local subagent | parent identity 전달, 권한 상한, bounded result join, conversation/run 격리, restart orphan reconciliation | `sidecar/test/runtime.test.js`, `tests/LIGClaw.Desktop.Tests/SubagentTests.cs` |
| Named Pipe 경계 | authenticated initialize, protocol·contract·required capability 협상, provider/MCP control RPC, Cline·replay run, Tool round trip, cancellation | `tests/LIGClaw.Sidecar.IntegrationTests/SidecarConversationTests.cs` |

`tests/fixtures/replays/basic-agent-events.json`은 normalized 기본 sequence의 최소 fixture다. fallback routing fixture는 `routing_changed`를 sequence 0으로 포함하고 Desktop 수신 경계를 실제로 통과해야 하며, unit-only event mapping으로 대체하지 않는다.

## 3. D-0 검증 명령

작업 중에는 변경 영역의 targeted test를 먼저 실행한다.

```powershell
npm test --prefix sidecar
dotnet test tests/LIGClaw.Desktop.Tests/LIGClaw.Desktop.Tests.csproj --no-restore
dotnet test tests/LIGClaw.Sidecar.IntegrationTests/LIGClaw.Sidecar.IntegrationTests.csproj --no-restore
```

D-0 병합 전에는 저장소 루트에서 전체 잠금 의존성·계약 생성 drift·TypeScript·.NET format/build/test를 한 번에 확인한다.

```powershell
./scripts/verify.ps1
```

검증 결과는 실행한 시점의 커밋과 함께 기록한다. 과거 `HANDOFF.md`나 acceptance 문서의 통과 수치를 현재 변경의 증거로 재사용하지 않는다.

### 2026-08-02 D-0 실행 결과

- `./scripts/verify.ps1` 통과
- Sidecar 84, Contracts 7, Application 20, Desktop 394, Named Pipe integration 1개 통과
- .NET build 경고 0개, 오류 0개
- contract generation drift와 TypeScript check/build 통과
- `@cline/llms → dify-ai-provider → @ai-sdk/provider-utils` 경로의 기존 low advisory 1건은 `HANDOFF.md`의 수동 Cline upgrade 항목으로 계속 추적

## 4. 외부 release gate

아래 항목은 저장소 자동 테스트로 통과했다고 표시하지 않는다. D단계 기능 개발과 별도 트랙으로 유지하지만, 배포 완료 판정에는 계속 필요하다.

| Gate | 현재 상태 | 완료 증거 |
|---|---|---|
| Windows 10 22H2 실기 | 대기 | 표준 사용자 VM/장치에서 시작·트레이·Credential Manager·SQLite·Sidecar 복구·주요 Windows Tool 결과를 기록 |
| Windows 11 실기 | 부분 자동화, release matrix 대기 | 실제 설치 package에서 Windows 11 전용 shell/화면·음성 경로와 주요 Tool smoke 기록 |
| production-trusted 서명·설치 수명주기 | 외부 인증서와 신뢰 체인 대기 | 깨끗한 표준 사용자 계정에서 설치 → 시작프로그램 → App Installer 업데이트 → 제거, SignTool 검증과 release manifest 보존 |
| 대표 앱 UIA | provider matrix 대기 | 메모장·계산기·설정과 승인된 사내 앱에서 inspect/invoke/set-value/send-text, identity·foreground·focus·password·provider hang 결과 기록 |
| 화면 OCR/STT/TTS·Explorer command | SDK/package identity 환경 대기 | signed/unsigned MSIX에서 권한·언어팩·오디오·picker 취소·보호 콘텐츠·다중 모니터와 native `IExplorerCommand` 등록 확인 |
| physical sleep/resume | 수동 필수 | Windows 10/11 각각 scheduler misfire·lease·Sidecar 복구 결과를 release evidence에 기록 |

상세 절차와 기존 증거는 `PHASE5_SECURITY_ACCEPTANCE.md`, `HANDOFF.md`, `runbooks/phase5-msix-release.md`, ADR 0028·0033·0034를 따른다. 외부 gate가 남아 있는 동안 구현 완료와 배포 완료를 같은 상태로 표시하지 않는다.

## 5. D단계 변경 규칙

- Capability와 Workflow는 위 Tool·정책·감사 경계를 우회하지 않는다.
- provider, MCP, job, subagent 동작 변경은 기존 기준선 테스트와 새 vertical-slice 테스트를 함께 통과해야 한다.
- protocol event나 handshake 결함은 schema/Named Pipe/replay를 포함한 결정적 회귀 테스트를 남긴다.
- prompt, file·clipboard 원문, credential, token은 fixture와 진단 출력에 넣지 않는다.
- 외부 gate 미완료를 코드 fallback, shell 우회, 승인 완화로 보상하지 않는다.
