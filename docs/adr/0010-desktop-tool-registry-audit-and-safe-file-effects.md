# ADR 0010: Desktop Tool registry, 감사 기록, 안전한 파일 효과

- 상태: 승인
- 날짜: 2026-07-26

## 배경

Phase 2는 창 닫기, 파일 이동, 클립보드 접근처럼 실제 사용자 상태에 영향을 주는 기능을 추가한다. Sidecar가 보낸 이름·위험도·경로를 그대로 실행하거나 각 기능이 별도 승인 경로를 만들면 권한 우회, 대상 바꿔치기, 덮어쓰기, 감사 누락이 생길 수 있다. 파일 작업은 긴 경로, reparse point, 부분 실패와 취소도 함께 다뤄야 한다.

## 결정

1. 모든 내장 Windows Tool은 Desktop의 `WindowsToolRegistry`에 adapter로 등록한다. registry descriptor는 canonical 이름, 위험도, capability, timeout, 우선순위를 가진다.
2. 실행은 기존 `ToolInvocationCoordinator`의 run identity 확인, 정책 판정, canonical preview, 사용자 승인, adapter 실행 순서를 반드시 통과한다. Sidecar 위험도와 Desktop adapter 위험도가 다르면 실행하지 않는다.
3. 승인 결정과 실행 결과는 Desktop 단독 SQLite의 append-only `approval_decisions`, `tool_executions`에 저장한다. 입력 payload, 클립보드 원문, 파일 내용은 감사 기록에 저장하지 않고 사용자용 요약만 저장한다.
4. 파일 경로는 Desktop에서 절대 경로로 정규화하고 각 reparse segment의 최종 대상을 해석한다. 승인 시점 identity를 실행 직전에 다시 비교한다.
5. Phase 2 파일 쓰기는 한 번에 최대 20개, 덮어쓰기 금지, 항목별 결과를 기본값으로 한다. UNC, 볼륨 루트, Windows, Program Files, ProgramData 쓰기는 이 위험 등급에서 허용하지 않는다.
6. 복사는 취소 가능한 비동기 스트림과 `CreateNew`를 사용한다. 이동·이름 변경·복사는 성공 결과의 identity와 경로를 `undo_entries`에 기록하며, 현재 파일이 바뀌지 않았을 때만 되돌린다. 휴지통 이동은 Windows Recycle Bin 복구 기능을 사용한다.
7. 클립보드 읽기는 쓰기가 아니어도 민감 컨텍스트이므로 R1 승인을 요구한다. 읽은 원문은 결과에만 포함하고 감사·진단에는 남기지 않는다.
8. 임의 shell, 실행 파일 경로, 영구 삭제는 등록하지 않는다.

## 결과

- 앱·파일·클립보드 효과가 하나의 정책 및 감사 경로를 사용한다.
- 모델이 위험도를 낮추거나 승인 뒤 대상을 바꿔도 Desktop에서 거부된다.
- 파일 작업은 충돌과 부분 실패를 구조화해 보고하며 가능한 작업은 Activity 화면에서 되돌릴 수 있다.
- 보호 위치나 네트워크 위치가 필요한 작업은 후속 위험 등급과 별도 preview 정책 없이는 지원되지 않는다.

## 검토한 대안

- Sidecar에서 경로와 승인을 판정: 교체 가능한 비신뢰 프로세스가 Windows 권한을 소유하게 되어 제외했다.
- PowerShell/shell 하나로 파일 기능 제공: 입력 범위와 영향 검증이 불가능해 제외했다.
- 덮어쓰기 허용 후 백업: 충돌 시 기본 동작이 파괴적이므로 Phase 2에서는 금지하고 후속 명시 계약으로 미룬다.
