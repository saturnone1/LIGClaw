# Phase 3 durable 알림 예약 Tool

- `schedule.create.v1` (R1): 제목·본문, 오프셋 없는 현지 시작 시각, Windows time zone ID, `once`/`daily`/`weekly`, 간격과 놓친 실행 정책을 preview한 뒤 저장한다.
- `schedule.list.v1` (R1): 활성 예약 또는 완료·취소를 포함한 예약을 승인 후 모델에 전달한다.
- `schedule.cancel.v1` (R1): `schedule.list`가 반환한 ID의 활성 예약을 승인 후 취소한다.

Sidecar는 자연어를 구조화할 뿐 시간을 확정하거나 실행하지 않는다. Desktop이 time zone과 다음 실행 instant를 다시 계산하고 SQLite schema 5의 `scheduled_jobs`, `job_runs`에 상태 전이를 기록한다. 지원하는 놓친 실행 정책은 `skip`, `run_once_on_resume`, `ask`다.

현재 예약 action은 생성 승인에서 고정된 로컬 Windows 알림뿐이다. 앱 재시작 때 실행 중이던 run은 `interrupted`로 기록하며, `ask`는 메인 화면의 **예약 관리**에서 **지금 실행** 또는 **건너뛰기**로 처리한다. 같은 화면에서 예약 조회·수정·삭제와 완료·취소 이력 확인이 가능하다.

저장된 별칭·선호를 Windows Tool 인자에 사용할 때 Sidecar는 `$alias:<키>` 또는 `$preference:<키>`를 전달할 수 있다. Desktop은 승인 preview 전에 값으로 해석하고 승인된 값을 실행까지 고정하며, 해석 값 자체는 Sidecar에 반환하지 않는다.
