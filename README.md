# LIGClaw

Windows 사용자 세션에 상주하며 반복 작업을 안전하게 대신하는 로컬 우선 개인 비서입니다.

Phase 0 walking skeleton을 완료했으며 Phase 1 진입 전 제품 디자인 기반을 적용했습니다. WPF Desktop이 Node.js Sidecar의 생명주기를 소유하고, 사용자별 Windows Named Pipe에서 버전드 JSON-RPC handshake와 heartbeat를 수행합니다.

## 현재 구현 범위

현재 Sidecar는 대화, 취소, 이벤트 스트리밍과 OpenAI 호환 모델 연결을 제공합니다. 설정에는 특정 공급자 선택 없이 `Base URL`, `API Key`, `Model`만 표시하며 API 키는 Windows 자격 증명 관리자에 저장합니다. 대화와 agent event는 Desktop이 소유하는 로컬 SQLite에 저장되며 최근 대화를 다시 열 수 있습니다. 읽기 전용 `system.get_status.v1`은 Sidecar 요청을 Desktop이 실행 ID, 중복 여부, 위험도, Windows capability까지 다시 검증한 뒤 실행하는 첫 Windows Tool입니다. 일반 화면은 요청 입력, 진행 상태, 답변에만 집중하며 내부 버전 같은 개발 정보는 노출하지 않습니다. Desktop은 사용자 세션당 하나만 실행되며 두 번째 실행이나 사용자가 설정한 빠른 호출 단축키는 기존 요청 창을 복원합니다. 기본값은 `Ctrl + Alt + Space`이고 충돌 시 다른 조합을 선택할 수 있습니다. 나머지 Windows Tool, 메모리, 예약 작업, MCP는 기능을 막아 둔 것이 아니라 아직 구현되지 않았습니다. 라이선스·계정·모델별 feature flag나 호출 quota는 없습니다.

Desktop은 실행 시 실제 Windows build를 감지합니다. Windows 10과 11에서 계약이 같은 Win32 기능은 공유하고, Windows 11 전용 API처럼 차이가 있는 기능만 capability 기반 adapter로 선택합니다.

다음 항목은 기능 제한이 아니라 프로세스 안정성과 보안을 위한 경계입니다.

- IPC payload 최대 4 MiB와 header 최대 8 KiB
- 연결 10초, heartbeat 응답 5초 timeout
- 동일 Windows 사용자만 접근 가능한 Named Pipe
- 계약 버전과 hash가 다른 Sidecar 연결 거부
- 진단 UI 최대 100개 항목, 항목당 2,048자 표시
- 한 요청의 agent 반복 최대 16회(무한 반복 방지)

화면 이미지나 대용량 파일은 향후 IPC에 직접 싣지 않고 승인된 임시 resource handle로 전달해 이 경계를 유지합니다.

## 요구 환경

- Windows 10 또는 Windows 11. 기술적 최소 target은 Windows 10 1809(build 17763)이며 Windows 10 배포 지원은 실기 검증 후 확정
- .NET SDK 10.0.103 이상 패치 버전
- Node.js 24
- PowerShell 7 또는 Windows PowerShell 5.1

대화 기록은 `%LOCALAPPDATA%\LIGClaw\Data\ligclaw.db`에 저장됩니다. API 키는 이 파일에 저장되지 않습니다.

## 검증

```powershell
./scripts/verify.ps1
```

## 실행

```powershell
./scripts/run.ps1
```

Sidecar를 먼저 빌드한 다음 Desktop을 실행합니다. 평소 말하듯 요청을 입력하고 진행 상태와 답변을 확인할 수 있습니다. 창을 닫으면 처음 한 번 안내한 뒤 트레이에 상주하고, 트레이 아이콘을 더블 클릭하면 다시 열립니다. 실제 종료는 트레이 메뉴에서 수행합니다. 문제 해결 정보는 기본적으로 접혀 있으며 필요할 때만 펼칠 수 있습니다. 모델을 연결하면 대화 요청은 설정에 입력한 OpenAI 호환 API endpoint로 전송됩니다.

설정에서 `Windows 시작 시 자동 실행`을 선택하면 현재 사용자 계정의 시작프로그램에 등록되고, 다음 로그인부터 창을 띄우지 않은 채 트레이에서 준비합니다. 관리자 권한이나 시스템 전체 설정은 사용하지 않습니다.

실제 프로세스 handshake, heartbeat, 강제 종료 후 자동 재시작과 고아 프로세스 정리는 다음으로 확인합니다.

```powershell
./scripts/smoke-sidecar.ps1
```

다른 PC에서 작업을 이어갈 때는 [개발 인수인계](docs/HANDOFF.md)를 먼저 확인하십시오. 구현 범위와 단계는 [구현 계획](docs/IMPLEMENTATION_PLAN.md), 시각 언어와 아이콘 원칙은 [디자인 시스템](docs/DESIGN_SYSTEM.md), 중요한 결정은 [ADR](docs/adr/)을 참고하십시오.

## 현재 구조

```text
src/LIGClaw.Desktop       WPF UI와 Sidecar supervisor
src/LIGClaw.Application   use case와 port (확장 예정)
src/LIGClaw.Domain        순수 도메인 모델 (확장 예정)
src/LIGClaw.Contracts     생성된 JSON-RPC 계약과 framing
sidecar                   Replay 및 Cline agent runtime adapter
contracts                 schema-first RPC/Tool 계약
tests                     결정적인 계약 테스트
```
