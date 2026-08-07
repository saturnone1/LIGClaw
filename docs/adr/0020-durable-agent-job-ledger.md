# ADR 0020 — Durable agent job ledger

## 결정

Desktop SQLite schema 10에 알림 예약과 분리된 `agent_jobs`·`agent_job_runs` ledger를 둔다. Agent job은 prompt, 현지 시작 시각·시간대·반복·misfire 정책, 선택 모델 profile ID, 실행 제한 시간, 최대 시도 횟수, 결과 문자 상한과 출처를 생성 시점 snapshot으로 저장한다.

Scheduler는 최대 두 실행만 동시에 claim하고 실행마다 독립 run ID와 lease를 발급한다. 실행 성공·실패·중단과 bounded 결과는 run ledger에 남긴다. 실패는 구성된 횟수까지 bounded backoff로 재시도하며, 프로세스 재시작 때 running run은 `interrupted`로 종료하고 남은 시도 범위 안에서 다시 pending 처리한다.

알림 scheduler와 agent scheduler는 실행기와 테이블을 공유하지 않는다. Agent executor는 결과만 반환하며 Windows 효과는 예약 생성 승인으로 자동 허용하지 않는다. 후속 Sidecar 연결에서도 Tool 호출은 실행 시점 Desktop 정책을 다시 통과해야 한다.

## 이유

- 알림 표시 실패와 모델 실행 실패의 상태·재시도 의미가 다르다.
- 프로세스 종료, 절전, 모델 장애 뒤에도 실행 이력과 부분 결과를 결정적으로 복구해야 한다.
- 예약 생성 권한이 미래의 Windows 효과에 포괄적으로 승계되는 것을 막는다.

## 결과

- 단발·매일·매주 agent job은 Windows 시간대와 DST 정책을 재사용한다.
- 실행 시간은 30~3,600초, 시도는 1~5회, 결과는 1,000~100,000자로 제한한다.
- 저장·claim·완료·재시작 복구는 모델 runtime 및 UI와 독립적으로 테스트할 수 있다.
- 관리 UI, schema-first Tool과 실제 background model executor는 같은 repository 경계 위에 후속 연결한다.
