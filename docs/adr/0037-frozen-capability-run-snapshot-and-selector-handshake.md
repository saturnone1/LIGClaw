# ADR 0037: Frozen Capability run snapshot과 selector handshake

- 상태: 승인
- 날짜: 2026-08-02

## 배경

D-1은 Desktop SQLite에 immutable Capability version, active pointer, activation history와 active-only FTS 후보를 추가했다. D-2에서는 이 정의를 Cline `AgentTool`과 provider prompt에 투영해야 한다. 단순히 Sidecar가 SQLite의 현재 active 상태를 조회하거나 `conversation.start`의 후보를 자체 선택하게 두면 현재 run 도중 activation 변경이 섞이고, Desktop이 비후보·stale version·Tool allowlist를 독립적으로 검증할 수 없다.

현재 IPC는 Sidecar가 `tool.invoke` notification을 보내고 Desktop이 `tool.result` RPC로 응답하는 방향을 이미 지원한다. Sidecar발 일반 request handler를 새로 추가하지 않고 이 검증된 왕복을 control-plane handshake에도 재사용할 수 있다.

## 결정

### Run 시작 시 frozen snapshot

- Desktop은 run 시작마다 한 SQLite statement에서 active FTS 후보와 exact immutable version을 함께 읽는다.
- 후보는 최대 8개이고 stable rank/id 순서다. draft, latest proposal, package source와 secret은 사용하지 않는다.
- Desktop은 현재 Windows profile의 Tool inventory, canonical risk, prompt-v1 bounds, content hash를 다시 검증한 뒤 `CapabilityRuntimeSnapshot`을 만든다.
- Desktop은 실제 MCP 연결 상태를 포함한 현재 Tool availability와 required Tools를 교차 검증한다. Sidecar도 투영할 수 없는 후보 하나만 격리하고 다른 후보와 일반 run을 유지한다.
- snapshot hash는 후보 순서, exact ID/version/content hash, catalog revision, model Tool name, display metadata, instructions, input schema, required Tool과 maximum risk를 length-prefixed SHA-256으로 결합한다.
- 후보는 stable rank 순서로 실제 UTF-8 serialized snapshot 2 MiB 예산 안에서만 추가한다. Sidecar도 같은 aggregate 상한을 재검증해 4 MiB RPC envelope와 provider 입력에 여유를 남긴다.
- snapshot이 sidecar start 요청에 전달된 뒤 현재 run authorization에는 깊은 복사로 고정한다. catalog activation/rollback은 현재 run에 hot-push하지 않는다.
- persistence나 snapshot 생성이 실패하면 Capability 후보는 fail-closed 된다. 일반 대화의 기존 Tool 경로를 사용할 수 있는지 여부는 고정된 empty snapshot 상태로 명시하며 stale/부분 snapshot은 보내지 않는다.

### Selector handshake

- Sidecar는 후보별 deterministic `cap_<24 lowercase hex>` model Tool을 생성한다.
- 모델이 후보 Tool을 선택하면 Sidecar는 기존 `tool.invoke → tool.result` bridge로 `capability.select.v1`을 먼저 요청한다.
- selector payload는 snapshot hash, model Tool name과 exact ID/version/content hash/catalog revision만 포함하며 사용자 입력이나 Capability instructions를 복제하지 않는다.
- Desktop은 현재 run의 frozen snapshot에 exact match가 있을 때만 selection을 승인한다. 비후보, stale hash/revision, 다른 run, duplicate Tool call과 한 run 내 재선택은 고정 오류로 거부한다.
- Desktop은 capability context나 selector payload를 해석하기 전에 최초 수신 Tool call ID를 공통 run policy에 예약한다. 검증을 통과한 continuation만 한 번 claim할 수 있고 선행 거부도 고정 audit를 남겨 같은 ID의 수정 재시도를 막는다.
- selector는 Windows effect가 아니며 Windows adapter registry로 전달하지 않는다. 승인 성공 뒤에만 Sidecar와 Desktop의 run authorization에 같은 exact selection을 고정한다.

### 선택 후 Tool 제한

- Sidecar는 선택된 instructions를 provider 전용 `prepareTurn` projection에만 넣는다. Desktop transcript, Cline conversation history, diagnostic log와 SQLite message에 instructions를 추가하지 않는다.
- Sidecar는 `beforeModel`에서 모델에 보이는 Tool을 selected Capability의 `requiredTools`로 축소하고 `beforeTool`에서도 같은 allowlist를 강제한다.
- 선택 후 일반 Tool 요청에는 `CapabilityInvocationContext`가 반드시 붙는다. Desktop은 snapshot hash, exact selection과 context를 비교하고 required Tool 밖 호출을 effect pipeline 전에 거부한다.
- allowlist를 통과해도 canonical risk, target 재해석, preview, 사용자 승인, timeout, at-most-once Tool call, 실행과 감사는 기존 Desktop pipeline이 다시 판정한다.
- 지속 R1 grant scope는 canonical Tool scope와 함께 snapshot hash, model Tool name, capability ID/version/content hash와 catalog revision에 결합한다.
- 선택 전의 기존 일반 Tool 동작은 회귀 호환을 위해 유지한다. 선택 후 context 없는 호출은 거부한다.
- 후보가 있으면 legacy memory 단축 경로와 JSON text fallback으로 selector를 우회하지 않는다.

### Session 수명

- Sidecar는 전달받은 root hash를 그대로 신뢰하지 않고 canonical snapshot hash와 model Tool name을 다시 계산한다.
- Cline session key는 immutable provider routing key와 recomputed Capability snapshot hash를 함께 포함한다.
- 같은 conversation이라도 snapshot hash가 바뀌면 bounded Desktop history로 새 AgentRuntime을 만든다. 기존 active run은 중단하지 않고 terminal 뒤 retire한다.
- 후보가 있는 같은-hash run이라도 Desktop history가 없으면 이전 selector Tool result를 재사용하지 않도록 새 AgentRuntime을 만든다.
- rollback으로 같은 exact version이 다시 active가 되어도 catalog revision이 증가하므로 새 snapshot/session이 된다.

## 계약과 저장

- protocol 1.18에 `CapabilityRuntimeCandidate`, `CapabilityRuntimeSnapshot`, `CapabilityInvocationContext`, `capability.select.v1` schema를 추가한다.
- D-2 candidate kind는 계약부터 `prompt`로 제한한다. Workflow와 Connector는 해당 runtime의 discriminator와 검증 경계를 함께 추가할 때 protocol을 올린다.
- 계약은 `contracts/`가 단일 원천이며 C#과 TypeScript 생성물을 함께 커밋한다.
- D-2 핵심 snapshot에는 별도 DB migration이 필요 없다. 후속 audit metadata가 필요하면 schema 14에서 snapshot hash와 exact identity/revision만 저장하며 instructions, input, prompt와 secret은 저장하지 않는다.

## 결과

- activation 변경은 다음 run에만 반영되어 run 중 동작이 결정적이다.
- Sidecar가 stale Capability Tool을 보유하거나 모델이 비후보 이름을 hallucinate해도 Desktop이 독립적으로 거부한다.
- Capability는 기존 Tool 정책의 상위 projection일 뿐 새 effect/권한 경로가 아니다.
- snapshot 크기와 후보 수가 제한되고 secret-free이므로 폐쇄망 IPC와 crash replay 범위가 명확하다.

## 대안

- Sidecar가 SQLite catalog를 직접 읽는 방식은 저장 소유권과 secret/process 경계를 깨므로 채택하지 않는다.
- catalog 변경을 running session에 hot-push하는 방식은 현재 run 불변성과 replay를 깨므로 채택하지 않는다.
- Sidecar발 신규 reverse request transport는 기존 bridge와 중복되며 인증·취소·at-most-once 표면을 넓히므로 채택하지 않는다.
- Capability가 실제 Tool을 감싸 직접 실행하는 방식은 Desktop 정책을 우회하므로 채택하지 않는다.
