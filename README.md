# LIGClaw

Windows 사용자 세션에 상주하며 반복 작업을 안전하게 대신하는 로컬 우선 개인 비서입니다.

현재는 Phase 0 walking skeleton 단계입니다. WPF Desktop이 Node.js Sidecar의 생명주기를 소유하고, 사용자별 Windows Named Pipe에서 버전드 JSON-RPC handshake와 heartbeat를 수행합니다.

## 현재 구현 범위

현재 Sidecar capability는 `health.ping`뿐입니다. Cline/LLM, 대화, Windows Tool, 트레이, 메모리, 예약 작업, MCP는 기능을 막아 둔 것이 아니라 아직 구현되지 않았습니다. 라이선스·계정·모델별 feature flag나 호출 quota는 없습니다.

다음 항목은 기능 제한이 아니라 프로세스 안정성과 보안을 위한 경계입니다.

- IPC payload 최대 4 MiB와 header 최대 8 KiB
- 연결 10초, heartbeat 응답 5초 timeout
- 동일 Windows 사용자만 접근 가능한 Named Pipe
- 계약 버전과 hash가 다른 Sidecar 연결 거부
- 진단 UI 최대 100개 항목, 항목당 2,048자 표시

화면 이미지나 대용량 파일은 향후 IPC에 직접 싣지 않고 승인된 임시 resource handle로 전달해 이 경계를 유지합니다.

## 요구 환경

- Windows 10/11
- .NET SDK 10.0.103 이상 패치 버전
- Node.js 24
- PowerShell 7 또는 Windows PowerShell 5.1

## 검증

```powershell
./scripts/verify.ps1
```

## 실행

```powershell
./scripts/run.ps1
```

Sidecar를 먼저 빌드한 다음 Desktop을 실행합니다. 창에서 runtime 연결 상태와 제한된 진단 로그를 확인하고 재시작 동작을 시험할 수 있습니다.

실제 프로세스 handshake, heartbeat, 강제 종료 후 자동 재시작과 고아 프로세스 정리는 다음으로 확인합니다.

```powershell
./scripts/smoke-sidecar.ps1
```

구현 범위와 단계는 [구현 계획](docs/IMPLEMENTATION_PLAN.md), 중요한 결정은 [ADR](docs/adr/)을 참고하십시오.

## 현재 구조

```text
src/LIGClaw.Desktop       WPF UI와 Sidecar supervisor
src/LIGClaw.Application   use case와 port (확장 예정)
src/LIGClaw.Domain        순수 도메인 모델 (확장 예정)
src/LIGClaw.Contracts     JSON-RPC 계약과 framing
sidecar                   교체 가능한 Node.js agent runtime 경계
contracts                 schema-first RPC/Tool 계약
tests                     결정적인 계약 테스트
```
