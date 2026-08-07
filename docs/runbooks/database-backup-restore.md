# 로컬 데이터베이스 백업과 복구

LIGClaw 데이터베이스는 `%LOCALAPPDATA%\LIGClaw\Data\ligclaw.db`에 있으며 대화, 실행 감사, undo, 개인 기억, 예약과 예약 실행 이력을 포함한다. API 키는 포함하지 않고 Windows 자격 증명 관리자에 별도로 저장한다.

## 백업

1. 트레이 메뉴에서 LIGClaw를 종료한다.
2. `ligclaw.db`, 존재하는 경우 `ligclaw.db-wal`, `ligclaw.db-shm`을 동일한 백업 폴더에 함께 복사한다.
3. 백업 폴더를 사용자 개인 데이터로 취급하고 접근 권한을 제한한다.

앱 실행 중 DB 파일 하나만 복사하면 WAL의 최근 변경이 빠질 수 있으므로 지원하지 않는다.

## 복구

1. LIGClaw를 종료한다.
2. 현재 `Data` 폴더를 별도 위치에 보관한다.
3. 백업한 DB와 같은 세트의 WAL/SHM 파일을 `Data` 폴더에 복원한다.
4. LIGClaw를 시작해 최근 대화, 기억 관리와 예약 관리 화면을 확인한다. 앱이 꺼져 있던 동안 지난 예약은 저장된 `skip`, `run_once_on_resume`, `ask` 정책으로 복구된다.

앱이 현재 코드보다 높은 schema version을 발견하면 DB를 수정하지 않고 초기화를 실패시킨다. 이 경우 원본을 보존하고 더 최신 LIGClaw 빌드로 복구한다.
