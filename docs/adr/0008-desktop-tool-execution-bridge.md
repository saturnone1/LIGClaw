# ADR 0008: Execute Windows Tools only through the Desktop bridge

## Status

Accepted for Phase 1 on 2026-07-25.

## Decision

- Protocol 1.3 adds Sidecar `tool.invoke` notifications and Desktop `tool.result` requests correlated by `toolCallId`, `conversationId`, and `runId`.
- Sidecar exposes model-safe tool names such as `system_get_status`, then maps them to versioned canonical Desktop names such as `system.get_status.v1`.
- Desktop validates the active conversation, canonical allowlist, declared risk, and platform capabilities before it executes any Windows API.
- Tool adapters declare required capabilities and priority. The host selects the highest-priority compatible adapter, allowing Windows 10 and Windows 11 implementations to coexist without version checks in agent code.
- Only R0 read-only Tools can auto-run in this slice. R1 and above are rejected until the approval UI and policy persistence path exists.
- Tool waits have timeout and cancellation behavior. Sidecar disconnect rejects every pending call. 사람의 승인 검토 시간과 Desktop adapter의 실제 실행 timeout은 서로 다른 책임으로 취급한다. Sidecar의 모든 Desktop Tool은 한 곳의 대화형 응답 정책을 사용하므로 Tool별 magic number가 승인 대화를 조기에 취소하지 않으며, 실제 Windows 작업은 각 Desktop adapter의 더 짧고 동작별인 timeout을 계속 적용한다.
- 일부 OpenAI 호환 모델이 native Tool call 대신 Tool 인자만 JSON 텍스트로 반환하는 경우, Sidecar는 제한된 호환 보정을 적용한다. 전체 응답이 단일 JSON 객체이고 사용자 문장에 해당 동작의 명시적 의도가 있으며, 허용된 필드 집합과 값 범위를 모두 만족하는 `app.launch.v1`, `memory.remember.v1`, `schedule.create.v1`만 canonical Desktop 요청으로 변환한다. 변환된 R1 요청도 일반 Tool 호출과 동일한 Desktop 승인·정책·감사 경로를 통과한다. 조건이 모호하거나 추가 필드가 있으면 자동 실행하지 않는다.
- 모델이 성공 종료하면서 공백만 반환해도 Sidecar는 빈 대화 항목을 만들지 않고, Tool 실행 여부에 맞는 제한된 사용자 안내를 남긴다. JSON 호환 보정 중 사용자가 취소하면 완료로 바꾸지 않고 `run_cancelled`를 유지한다.

## Consequences

- Sidecar and model output cannot directly invoke Win32, shell commands, or arbitrary executables.
- `system.get_status.v1` uses the common Windows 10/11 desktop API path and returns only release, build, architecture, local time, time zone, and power source.
- A compromised or stale Sidecar request cannot execute a Tool outside the currently active conversation.
- New Windows Tools require a schema, Desktop adapter, capability declaration, policy risk, deterministic bridge test, and Windows 10/11 compatibility test.
- 호환 보정은 임의 Tool 이름이나 위험도를 모델 JSON에서 받지 않으며, Sidecar에 고정된 최소 allowlist만 사용할 수 있다.
