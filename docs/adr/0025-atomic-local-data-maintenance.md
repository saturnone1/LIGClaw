# ADR 0025: 원자적 로컬 데이터 백업·복원·정리

SQLite 백업은 열린 Desktop 연결의 `BackupDatabase` API로 새 파일에 생성하고 `integrity_check`와 schema version을 확인한 뒤 원자적으로 이름을 바꾼다. 복원은 실행 중 DB를 교체하지 않고 검증된 `.restore.pending` 파일로 staging한 뒤 다음 Desktop 초기화 전에 적용한다. 기존 DB와 WAL/SHM은 시각이 붙은 `before-restore` 백업으로 보존한다.

자동 정리는 대화·기억 원문을 삭제하지 않는다. 사용자가 명시적으로 실행한 보존 기간 정리는 오래된 승인·실행 감사, 만료 권한, 완료된 undo·Agent job·subagent 실행 ledger만 transaction으로 제거한다. API 키는 어느 데이터 파일에도 포함되지 않는다.
