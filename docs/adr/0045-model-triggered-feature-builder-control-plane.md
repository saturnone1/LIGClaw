# ADR 0045: 모델이 여는 Desktop-owned Feature Builder control-plane

> 상태: 채택 · Phase A/B 제어기와 ACK-first Shell handoff 구현·집중 검증 완료
> 기준일: 2026-08-02
> 관련 계획: `../D_STAGE_PLAN.md`
> 선행 결정: `0036-retain-cline-sidecar-and-desktop-owned-capability-control-plane.md`, `0040-desktop-owned-feature-builder-proposal-pipeline.md`

## 맥락

메일은 범용 기능 추가 방식의 한 예시일 뿐이다. Chat에서 사용자가 “이런 기능을 추가해 줘”라고 말하면 일반 Cline agent가 그 의도를 알아차리고 기존 Feature Builder로 넘길 수 있어야 한다. 그러나 모델이 요구사항을 다시 작성해 Tool payload로 보내게 하면 원문과 다른 기능을 만들거나 prompt·secret을 별도 IPC, 결과, audit 또는 diagnostic 경로에 복제할 수 있다.

따라서 모델은 Builder를 열 최소 의도만 말하고, Desktop은 이미 저장한 현재 run의 정확한 사용자 입력을 직접 결합한다. Sidecar는 계속 교체 가능한 Cline SDK agent runtime이고 Builder 상태, 정책, 저장과 UI effect는 Desktop이 소유한다.

## 결정: Protocol 1.24의 최소 Tool 계약

일반 Cline Tool catalog에 `capability_builder_open`을 등록하고 Desktop canonical name을 `capability.builder.open.v1`로 고정한다. 이 계약이 실제 Protocol 1.24를 소유하며 contract hash는 `7a221a94e96ef061630a92382356761cea49acdf271d0503a915c31ccd013776`이다. ADR 0044가 미구현 attachment 계획에 적었던 1.24 숫자 예약은 효력이 없으며, attachment가 해동되면 구현 시점의 next available protocol을 사용한다.

Input은 `additionalProperties: false`이고 다음 한 필드만 허용한다.

- `intent`: `create | revise`

성공 result도 `additionalProperties: false`이고 다음 두 필드만 허용한다.

- `accepted`: literal `true`
- `intent`: 요청과 같은 `create | revise`

`requirement`, prompt, conversation content, Capability identity와 임의 추가 필드는 input과 result 모두에서 허용하지 않는다. Sidecar adapter는 schema 외에도 실행 직전 exact key를 검사하며, Desktop 응답이 요구사항이나 추가 필드를 반사하면 그 응답도 거부한다. 이 Tool은 `R0`이며 shell, mail, network 또는 파일 effect를 실행하지 않는다.

## 결정: Desktop이 exact persisted run input을 결합

Desktop controller는 invocation의 `(conversationId, runId)`와 정확히 일치하고 status가 `running`인 `conversation_runs.user_input`만 읽는다. 대화의 최신 행, 화면의 composer text, 모델이 만든 요약 또는 Tool input을 요구사항으로 대체하지 않는다. 읽기 전후에 같은 run이 여전히 active이고 취소되지 않았는지 다시 확인하며 기존 `FeatureBuilderPolicy`의 길이·secret·unsafe input 검사를 통과해야 한다.

성공 outcome은 서로 다른 두 값을 가진다.

1. Sidecar로 돌려보낼 requirement-free fixed-structure execution acknowledgement
2. exact persisted requirement를 가진 Desktop process-memory `RequestAfterAcknowledgement`

두 번째 값의 문자열 표현은 requirement와 run identity를 redaction한다. control-plane은 requirement를 새 DB row, Tool activity, audit, log 또는 diagnostic bundle에 기록하지 않는다. 기존 conversation row와 이 짧은 in-memory handoff만 원문을 가진다. Tool result ACK 뒤에는 Builder 화면에 원문을 prefill만 하며 session 생성과 proposal generation은 시작하지 않는다. 사용자가 화면에서 명시적으로 생성을 누를 때에만 기존 Feature Builder session 저장 경계가 적용된다.

## 결정: acknowledgement 뒤에만 UI effect

Shell 연결은 다음 순서를 반드시 지킨다.

1. exact canonical Tool name을 일반 Windows Tool coordinator보다 먼저 이 controller로 route한다.
2. controller의 `Execution`을 `tool.result`로 Sidecar에 제출하고 그 acknowledgement를 기다린다.
3. 성공 result가 확인된 뒤 exact accepted `(conversationId, runId, toolCallId)`, accepted run sequence, 명시적 cancellation tombstone과 현재 conversation/run context를 다시 검사한다. `ConversationSessionState.EndRun`은 정상 완료에도 내부 CTS를 취소하므로 token 상태만으로 정상 완료와 사용자 취소를 구분하지 않는다. Tool result를 받은 agent가 정상 완료해 active run이 ACK 응답보다 먼저 닫힌 경우에는 같은 current conversation에서 handoff를 허용하지만, 명시적으로 cancel됐거나 다른 conversation/run이 시작된 stale context는 거부한다.
4. Dispatcher가 실제 화면을 만들기 직전 같은 authorization을 한 번 더 검사한 뒤에만 `RequestAfterAcknowledgement`를 소비해 Builder로 이동하고 requirement를 prefill한다. 자동 send, session 시작 또는 proposal generation은 하지 않는다.
5. Tool failure, Sidecar result 전송 실패, stale/cancel 또는 shutdown이면 deferred request를 폐기하고 Builder를 열지 않는다.

즉 모델 실행 acknowledgement와 Desktop UI navigation은 하나의 result payload로 섞지 않는다. Phase A는 controller와 handoff type을, Phase B는 MainWindow의 exact-name route, 공용 result 제출 helper와 ACK 뒤 Builder 시작을 구현했다.

## 결정: fail-closed와 at-most-once

다음 입력은 요구사항을 읽거나 Builder를 열기 전에 고정 오류 코드로 거부한다.

- stale, completed 또는 cancelled run
- 같은 Tool call identity의 replay
- 같은 run에서 call ID만 바꾼 두 번째 open
- `R0`가 아닌 risk, `CapabilityContext`, 누락·추가·잘못된 payload
- 저장 행 불일치, non-running status, 저장소 오류 또는 Builder policy 위반 원문

Tool call identity는 기존 `ConversationRunController`의 shared control-plane prepare/claim 정책에서 먼저 소비한다. 따라서 잘못된 payload를 같은 ID로 고쳐 재시도할 수 없고, 같은 run의 다른 ID도 첫 성공 뒤에는 거부된다. 오류 text, output과 객체 문자열은 모델 payload나 persisted requirement를 echo하지 않는다.

`revise`는 모델이 target을 추측하도록 허용하지 않는다. 현재 구현에는 사용자가 고른 exact Capability/version/revision CAS target이 없으므로 `capability.builder.revise_target_required`로 거부한다. Shell에 명시적 target 선택 경계가 생기기 전까지 성공 경로는 `create`만 연다.

## 검증

- generated C#/TypeScript parity와 contract generation check
- Sidecar catalog/registration exact parity, input injection과 Desktop result reflection 거부
- 실제 SQLite exact prompt 보존, requirement-free fixed-structure result와 activity 0건
- stale/cancel, same·different call ID duplicate, context/non-R0/revise, non-running·unsafe prompt 결정적 회귀
- shared Tool invocation policy 회귀
- MainWindow exact route와 requirement-free fixed-structure result 제출, Sidecar ACK 뒤 exact identity·run sequence·명시적 cancellation tombstone·current context 재검사 후에만 Builder를 여는 source/구성 집중 회귀
- 실제 Sidecar process/Named Pipe deterministic fixture의 R0, exact conversation/run identity, intent-only input과 ACK round-trip

## 결과

- Chat의 자연어 기능 추가 요청은 메일 전용 코드 없이 일반 agent Tool로 Builder 진입을 요청할 수 있다.
- 모델은 기능 요구사항의 운반자나 저장 소유자가 아니며 Desktop이 사용자의 exact persisted input을 신뢰 기준으로 사용한다.
- no-effect acknowledgement와 user-visible navigation의 순서가 분리돼 전송 실패 때 유령 Builder 화면이 열리지 않는다.
- 기존 Sidecar/Cline SDK 구조와 Feature Builder proposal pipeline은 유지되고, 범용 비메일 통합 수직으로 이어지는 좁은 control-plane만 추가된다. 전체 UI·실제 모델·Named Pipe·활성화·다음 run 실행을 한 프로세스 시나리오로 묶은 단일 E2E라고 주장하지 않는다.

## 대안

- 모델이 `requirement` 문자열을 Tool input에 넣는 방식은 원문 변조와 중복 노출 때문에 채택하지 않는다.
- 메일 보내기 같은 전용 Chat Tool을 추가하는 방식은 범용 Capability Builder 목표와 다르므로 채택하지 않는다.
- Sidecar가 Builder session과 UI를 소유하는 방식은 Desktop-owned 정책·저장·effect 경계를 깨므로 채택하지 않는다.
- target 없는 `revise`를 create로 자동 변환하는 방식은 사용자 선택과 CAS identity를 잃으므로 채택하지 않는다.
