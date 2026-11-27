# ADR 0036: Cline Sidecar 유지와 Desktop 소유 Capability Control Plane

- 상태: 승인
- 날짜: 2026-08-02

## 배경

LIGClaw는 현재 `.NET 10 WPF Desktop + bundled Node.js Sidecar` 경계에서 `@cline/agents`, 모델 routing, Tool bridge, MCP session, Desktop 승인·감사·SQLite를 검증했다. 다음 D단계에서는 재빌드 없이 기능을 추가하는 Capability, 선언형 Workflow, Connection Profile, Feature Builder가 필요하다.

고정한 Cline SDK는 Node.js/TypeScript 패키지다. 이를 .NET으로 옮기는 것은 같은 SDK의 런타임 이전이 아니라 Cline을 제거하고 Agent loop를 다시 구현하는 별도 제품 결정이다. 또한 Capability가 정의, 권한, 비밀, 실행을 함께 소유하면 기존 Desktop Tool 정책을 우회하거나 Sidecar 장애가 영속 상태와 외부 효과를 손상시킬 수 있다.

이 ADR은 ADR 0001의 프로세스·데이터 경계와 ADR 0003의 Cline adapter 격리를 대체하지 않고 D단계 Capability Platform에 맞게 확장한다.

## 결정

### 프로세스와 UI 경계

- bundled Node.js/TypeScript Sidecar와 exact-pinned Cline SDK를 유지한다. Sidecar는 Cline Agent loop, 모델 호출·streaming·취소·fallback, prompt와 custom `AgentTool` projection, MCP session을 소유한다.
- Desktop은 계속 `.NET 10 WPF` 사용자 애플리케이션이다. Capability, Builder, Connection, Task 운영 화면은 기존 WPF Precision Workspace에 추가하며 현 단계에서 Blazor Hybrid로 이전하지 않는다.
- Sidecar는 교체 가능한 자식 프로세스로 남고 Desktop과 schema-first Named Pipe 계약으로만 통신한다. Desktop, Domain, Application은 Cline SDK type을 참조하지 않는다.

### Capability와 Workflow 소유권

- Desktop이 Capability version, draft, proposal, readiness, activation, rollback, Connection binding과 Workflow definition의 authoritative control plane을 소유한다.
- Desktop이 요청별로 후보 수와 크기가 제한되고 secret이 없는 immutable Capability projection을 만든다. Sidecar는 전달받은 exact ID, version, content hash와 허용 Tool 범위 안에서만 Capability를 선택·투영하며 정의나 활성 상태를 쓰지 않는다.
- Workflow parser, 정적 검증, 실행 계획, run/step ledger, resume와 재시도 판정은 Desktop이 소유한다. Sidecar의 Workflow Tool은 실행을 요청하고 bounded 결과를 Cline에 돌려줄 뿐 step을 해석하거나 직접 effect를 만들지 않는다.
- Capability 활성화는 실행 권한 부여가 아니다. 모든 effectful step은 기존 Desktop의 schema 검증, canonical target 해석, 위험도 판정, preview, 승인, 실행, 감사 경로를 다시 통과한다. Sidecar, model, Capability manifest, MCP annotation은 canonical risk나 최종 승인 결과를 정할 수 없다.

### 저장, 비밀과 실제 효과

- Desktop만 SQLite와 migration을 소유한다. Capability와 Workflow의 canonical version, 활성화, 실행 ledger, 감사는 Desktop SQLite에 저장하고 `.ligcap`은 검증된 import/export 형식으로만 사용한다.
- 비밀의 장기 저장은 Desktop의 Credential Manager가 독점한다. Sidecar에는 model 또는 승인된 MCP session에 꼭 필요한 값만 실행 범위로 전달할 수 있으며 저장하거나 로그에 남길 수 없다. Capability projection, package, SQLite, transcript, replay에는 secret을 포함하지 않는다.
- Windows, 파일, process와 신규 HTTP·SMTP·SQL Connection effect는 Desktop의 typed adapter만 실행한다. MCP transport와 session은 Sidecar에 남지만, 개별 MCP 호출은 Desktop이 승인한 exact connection/tool/scope 없이는 시작할 수 없다.

### 실행 snapshot과 변경 적용

- 각 run은 provider routing과 Capability projection의 hash가 결합된 immutable runtime snapshot을 사용한다. 실행 중 activation, rollback, Connection binding 변경은 현재 run에 반영하지 않고 다음 run부터 적용한다.
- Cline runtime의 Tool 구성이 생성 시 고정되므로 snapshot hash가 바뀌면 기존 conversation runtime을 안전하게 retire하고 bounded history로 새 runtime을 만든다. 활성 run에 Tool이나 prompt를 중간 삽입하지 않는다.

## 검토한 대안

- **Sidecar를 제거하고 all-.NET으로 통합:** 채택하지 않는다. 현재 Cline SDK를 사용할 수 없고 검증된 Agent loop, event, hook, restore, Tool projection을 별도로 재구현해야 하며 Desktop 장애 격리도 약해진다.
- **WPF를 Blazor Hybrid Dashboard로 즉시 교체:** 채택하지 않는다. Capability 경계와 무관한 UI 재작성으로 Windows shell·접근성·자동화 회귀 범위만 커진다.
- **Sidecar가 Capability 파일·SQLite·Workflow 실행을 소유:** 채택하지 않는다. 데이터 복구와 migration 주체가 둘이 되고 모델 런타임이 정책·승인 경계를 우회할 수 있다.
- **Capability 변경을 실행 중 session에 hot reload:** 채택하지 않는다. 한 run의 Tool/prompt 의미가 도중에 바뀌어 replay와 승인 binding이 비결정적이 된다.
- **Script, plugin, in-process DLL, Remote Runner를 초기 Executor로 제공:** 연기한다. typed Tool과 선언형 Workflow로 해결할 수 없는 실제 요구가 확인된 뒤 서명·hash pin, 별도 worker, 권한 상한, 배포·복구 모델을 각각 독립 ADR로 결정한다.

## 결과

- Cline의 Node.js 런타임 요구를 충족하면서 기존 프로세스 장애 격리와 exact dependency pin을 유지한다.
- Capability 추가·활성화·rollback은 본체 재빌드 없이 가능하지만, 새 기능도 기존 Desktop 권한과 감사 경로보다 넓은 권한을 얻지 못한다.
- Sidecar crash나 stale projection이 canonical 정의, 비밀, 영속 상태를 수정하거나 완료된 외부 effect를 임의 재실행할 수 없다.
- snapshot 변경 때 runtime 재생성과 bounded history restore 비용이 생기며, Desktop–Sidecar 사이에 명시적 projection·Workflow 계약과 호환성/replay 테스트가 필요하다.
- all-.NET, Blazor, Script·plugin, Remote Runner는 영구 금지가 아니다. 도입하려면 Cline 호환성, 프로세스·저장·권한 경계, 폐쇄망 배포와 복구 조건을 별도 ADR과 수직 검증으로 먼저 확정해야 한다.
