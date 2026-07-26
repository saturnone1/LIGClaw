# LIGClaw

Windows 사용자 세션에 상주하며 반복 작업을 안전하게 대신하는 로컬 우선 개인 비서입니다.

현재 통합 베타 버전은 **v0.6.0**입니다.

Phase 0 walking skeleton, Phase 1 상주 셸·대화, Phase 2 안전한 Windows 작업과 Windows 문맥 확장, Phase 3 기억·예약, Phase 4 제한 UI Automation, Phase 5 MCP·베타 배포 구현을 완료했습니다. Phase 5의 최종 배포 판정은 신뢰된 서명 체인이 있는 깨끗한 Windows 계정에서 설치·로그인 자동 시작·업데이트·제거를 확인하는 외부 release gate만 남아 있습니다. WPF Desktop이 Node.js Sidecar의 생명주기를 소유하고, 사용자별 Windows Named Pipe에서 버전드 JSON-RPC handshake와 heartbeat를 수행합니다. Windows 에이전트 기능의 완료·부분·계획·후속 범위는 `docs/WINDOWS_AGENT_CAPABILITY_AUDIT.md`에서 추적합니다.

## 현재 구현 범위

같은 대화의 후속 요청은 하나의 `conversationId`와 문맥을 유지하고 요청마다 새 `runId`만 발급합니다. 사용자가 `새 대화`를 선택할 때만 문맥을 분리하며, 대화 턴은 Desktop SQLite에 저장되어 앱 재시작 후에도 최근 대화를 이어갈 수 있습니다.

현재 Sidecar는 대화, 취소, 이벤트 스트리밍과 OpenAI 호환 모델 연결을 제공합니다. 설정에서 이름 있는 모델 프로필 여러 개와 기본→fallback 순서를 관리하며 API 키는 프로필별 Windows 자격 증명 관리자에 저장됩니다. 명시 선택 프로필은 자동 전환하지 않고, 허용된 공급자 장애에서만 실행별 불변 routing snapshot에 따라 fallback합니다. MCP는 HTTPS/loopback Streamable HTTP와 번들 샘플 RAG용 고정 stdio 실행 ID를 지원하고, Bearer 인증도 Windows 자격 증명 관리자에 저장합니다. 발견된 도구는 exact-name allowlist 이후에만 모델에 보이며 실제 호출은 Desktop의 R3 승인·감사 경로를 통과합니다. 대화와 agent event는 Desktop이 소유하는 로컬 SQLite에 저장되며 최근 대화를 다시 열 수 있습니다. 답변은 Markdown 원문을 보존하면서 WPF에서 표·제목·강조·목록·인용·코드로 렌더링합니다. 시스템 상태·Windows 알림, 창 목록·설치 앱 검색·등록 앱 실행·창 활성화·정상 닫기, Explorer 현재 폴더·선택 항목, 파일 검색·메타데이터·제한 텍스트 읽기·열기·복사·이동·이름 변경·휴지통·되돌리기, 클립보드 텍스트 읽기·쓰기, 명시적 개인 기억, durable 알림·Agent 작업, bounded 로컬 하위 Agent가 모델 → Sidecar → Desktop 경로로 연결돼 있습니다. 기억·알림·Agent 작업은 모델 없이 전용 관리 화면에서 조회하거나 관리할 수 있습니다. Desktop은 실행 ID, 중복 여부, registry의 canonical 위험도, Windows capability와 timeout을 다시 검증합니다. R1/R2 작업과 Explorer·파일·클립보드의 민감한 읽기는 실제 동작과 이유를 보여준 뒤 `이번 한 번` 승인해야 실행하며 승인과 결과는 원문 없이 로컬 실행 활동에 기록됩니다. 파일 쓰기는 절대·canonical 경로, reparse target, 승인 후 identity를 확인하고 최대 20개·덮어쓰기 금지·항목별 부분 실패를 적용합니다. 이동·이름 변경·복사는 대상이 바뀌지 않은 동안 실행 활동에서 되돌릴 수 있습니다. UNC와 Windows 보호 위치 쓰기, 임의 실행 파일·명령줄·shell, 영구 삭제는 제공하지 않습니다. 일반 화면은 요청 입력, 진행 상태, 답변에만 집중하며 내부 버전 같은 개발 정보는 노출하지 않습니다. Desktop은 사용자 세션당 하나만 실행되며 두 번째 실행이나 사용자가 설정한 빠른 호출 단축키는 기존 요청 창을 복원합니다. 기본값은 `Ctrl + Alt + Space`이고 충돌 시 다른 조합을 선택할 수 있습니다. 라이선스·계정·모델별 feature flag나 호출 quota는 없습니다.

B단계에서는 물리 디스크·BitLocker·Defender·방화벽·Windows Update 관측, 폴더·UTF-8 파일·ZIP 작업, 창 상태 변경, allowlist Windows 설정, 잠금·절전, Agent 작업 일시정지·재개·재시도와 로컬 데이터 백업·복원을 추가했습니다. exact-scope R1 동작은 한 번, 현재 대화 동안, 30일 중에서 승인 범위를 선택할 수 있습니다.

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
src/LIGClaw.Application   use case와 port
src/LIGClaw.Domain        순수 도메인 모델
src/LIGClaw.Contracts     생성된 JSON-RPC 계약과 framing
sidecar                   Replay 및 Cline agent runtime adapter
contracts                 schema-first RPC/Tool 계약
tests                     결정적인 계약 테스트
```
