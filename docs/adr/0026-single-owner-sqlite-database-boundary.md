# ADR 0026: 단일 소유 SQLite database 경계

Desktop의 `ConversationDatabase`만 SQLite connection, 직렬화 gate, schema 1~11 migration, pending restore 적용과 backup 무결성 검증을 소유한다. 대화·감사·기억·예약·Agent job·subagent repository는 이 경계가 빌려 주는 동일 connection에서 작업하며 개별 connection, migration 또는 SQLite 파일 교체를 만들지 않는다.

기존 `ConversationStore`는 repository 분리를 단계적으로 적용하는 동안 호환 facade로 유지한다. 이 변경은 DB 경로, schema version, SQL 데이터 형식이나 보존 정책을 바꾸지 않는다. 초기화 실패 시 connection을 즉시 닫고, 미래 schema·손상 backup을 거부하며, restore는 ADR 0025의 다음 시작 원자적 교체 방식을 유지한다.
