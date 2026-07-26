# Phase 2 Windows Tool 안내

Phase 2 Tool은 모두 Desktop registry와 정책 파이프라인을 통과한다. Sidecar가 위험도를 낮추거나 승인 뒤 대상을 바꾸면 실행하지 않는다.

## 시스템 관측

- `system.get_status.v1` (R0): Windows 버전·빌드·아키텍처·시간대·전원 소스를 반환한다.
- `system.get_storage_status.v1` (R0): 논리 볼륨의 종류·준비 상태·파일시스템·전체/여유 용량·사용률을 반환한다. 물리 디스크 SMART 건강 상태라고 표현하지 않는다.
- `system.get_resource_status.v1` (R0): 짧은 표본의 CPU 사용률, 논리 프로세서 수, 메모리 전체/여유/사용률과 업타임을 반환한다.
- `system.get_network_status.v1` (R0): 네트워크 사용 가능 여부와 최대 32개 어댑터의 이름·종류·상태·링크 속도를 반환한다. IP, MAC, DNS와 트래픽 내용은 포함하지 않는다.

세 Tool은 조회 전용이며 인자를 받지 않는다. 장치별 건강 정보, 네트워크 주소, 설정 변경은 권한과 개인정보가 다른 별도 Tool로 추가한다.

## 앱과 창

- `app.list_windows.v1` (R0): 보이는 최상위 창의 제한된 메타데이터를 최대 100개 반환한다.
- `app.search_installed.v1` (R0): 시작 메뉴 앱을 표시 이름으로 검색해 최대 20개 반환한다. 실행 경로와 명령줄은 반환하지 않는다.
- `app.launch.v1` (R1): Start Menu에 정확한 이름으로 등록된 앱만 실행한다.
- `app.activate.v1` (R1): `app.list_windows`가 반환한 현재 창 ID를 다시 확인한 뒤 앞으로 가져온다.
- `app.close.v1` (R2): 같은 창인지 다시 확인하고 `WM_CLOSE`만 요청한다. 프로세스를 강제 종료하지 않는다.

## 파일

- `explorer.get_context.v1` (R1): 가장 최근 활성 파일 탐색기의 현재 폴더와 선택 항목 경로를 승인 화면에 보여준 뒤 최대 20개 반환한다.
- `file.search.v1` (R0): 절대 경로 폴더에서 이름만 검색한다. 내용은 읽지 않으며 최대 100개, 깊이 8이다.
- `file.get_metadata.v1` (R0): 기존 파일·폴더 하나의 이름, 종류, 크기, 수정 시각, 읽기 전용 여부만 반환한다.
- `file.read_text.v1` (R1): 정확한 경로와 이유를 승인받고 UTF 텍스트를 최대 128 KiB 반환한다. 바이너리는 거부하며 승인 전후 identity를 다시 확인한다.
- `file.open.v1` (R1): 기존 파일 또는 폴더 하나를 Windows 기본 앱으로 연다.
- `file.copy.v1`, `file.move.v1` (R2): 파일 최대 20개를 기존 폴더로 처리한다. 덮어쓰지 않고 항목별 성공·실패를 반환한다.
- `file.rename.v1` (R2): 파일 하나를 같은 폴더에서 안전한 이름으로 변경한다.
- `file.recycle.v1` (R2): 최대 20개 파일·폴더를 Windows 휴지통으로 보낸다. 영구 삭제는 제공하지 않는다.
- `file.undo.v1` (R2): LIGClaw undo ID가 있고 작업 후 파일 identity가 바뀌지 않았을 때만 복사·이동·이름 변경을 되돌린다.

파일 쓰기는 절대 경로와 reparse 최종 대상을 승인 전후에 확인한다. UNC, 볼륨 루트, Windows, Program Files, ProgramData에는 쓰지 않는다. 장시간 복사는 Desktop timeout과 사용자 취소를 따른다.
Explorer 선택 경로와 파일 텍스트 원문은 진단·감사 요약에 저장하지 않는다.

## 클립보드

- `clipboard.read_text.v1` (R1): 승인 후 텍스트만 최대 32,768자 읽어 현재 모델 요청에 반환한다.
- `clipboard.write_text.v1` (R1): 제한된 미리보기 승인 후 텍스트 최대 32,768자로 클립보드를 교체한다.

클립보드 원문은 진단이나 감사 기록에 저장하지 않는다. 실행 활동에는 Tool 이름, 위험도, 승인 여부와 원문 없는 결과 요약만 남는다.
