# ADR 0012 — Desktop 소유 durable 알림 스케줄러

## 결정

예약의 해석 가능한 계약은 Sidecar가 만들지만 저장, 시간 검증, 실행 lease, 재시작 복구와 Windows 알림 표시는 Desktop이 소유한다. SQLite schema 5에 `scheduled_jobs`와 `job_runs`를 추가하고 Sidecar는 DB에 연결하지 않는다.

MVP action은 로컬 알림으로 제한한다. `schedule.create.v1`, `schedule.list.v1`, `schedule.cancel.v1`은 R1이며 생성·조회·취소 때 사용자 승인을 요구한다. 실행 시에는 생성 승인에서 고정된 제목과 본문만 표시하므로 별도 모델 호출이나 반복 승인을 요구하지 않는다. 향후 파일·앱 등 영향 있는 action을 예약한다면 생성 승인과 실행 시 승인을 분리해 새 계약으로 추가한다.

반복은 기준 현지 시각과 Windows time zone ID를 보존하는 `once`, `daily`, `weekly` 및 1~365 간격으로 표현한다. DST spring gap의 존재하지 않는 시각은 같은 날의 첫 유효 분으로 이동하고, fall overlap의 중복 시각은 먼저 발생하는 UTC instant를 선택한다. 따라서 반복은 고정 UTC 간격이 아니라 사용자가 정한 wall clock을 유지한다.

상대 예약은 `delayMinutes`(1~10,080분)로 별도 표현하며 `startLocal`/`timeZoneId`와 동시에 받을 수 없다. 모델이 현재 시각을 추측하거나 시간 계산을 반복하지 않도록 Sidecar는 사용자가 말한 지연 값만 전달하고, Desktop이 신뢰한 현재 시각과 로컬 Windows time zone으로 실제 첫 실행 시각을 계산한 뒤 승인 화면에 표시한다. 절대 현지 시각 예약은 기존처럼 `startLocal`과 `timeZoneId`를 함께 요구한다.

앱 시작 시 `running` 실행은 `interrupted` run으로 확정하고 원래 예정 시각을 복구한다. 실행 중에도 주기마다 만료된 lease를 `lease_expired`로 확정하고 다시 대기 상태로 돌려, 알림 호출 장애가 재시작 전까지 예약을 영구 정지시키지 않게 한다. 1분보다 오래 지난 예약은 시작·절전 복귀 여부와 관계없이 각 job의 정책에 따라 다음처럼 처리한다. 1분 이내 지연은 일반 타이머 지연으로 보고 정상 실행한다.

- `skip`: 놓친 회차를 `skipped`로 기록하고 다음 회차로 이동한다.
- `run_once_on_resume`: 가장 오래된 회차를 한 번 실행한 뒤 현재 이후의 다음 회차로 이동한다.
- `ask`: `awaiting_decision` 상태로 두고 예약 관리 화면에서 **지금 실행** 또는 **건너뛰기**를 선택하게 한다.

## 이유

- Sidecar나 모델 연결이 없어도 트레이 프로세스가 예약을 실행할 수 있다.
- 예정 시각, lease와 실행 결과를 한 SQLite transaction 경계에서 다뤄 중복 실행과 유실을 줄인다.
- UTC만 저장해 현지 반복이 DST에서 한 시간씩 이동하는 문제를 피한다.
- 놓친 실행을 임의로 결정하지 않고 사용자가 예약별 정책을 선택하게 한다.

## 결과

- LIGClaw가 실행 중일 때만 예약이 실행되며 Phase 1의 현재 사용자 시작프로그램 등록으로 로그인 후 복구한다.
- Windows 서비스나 Task Scheduler 등록, 관리자 권한은 사용하지 않는다.
- 예약 삭제는 실행 감사 보존을 위해 `cancelled` tombstone으로 남고 이후 실행 대상에서는 제외된다.
- 사용자는 모델 없이 예약 관리 화면에서 활성·완료·취소 예약을 조회하고 활성 예약을 수정·삭제하며 놓친 `ask` 예약을 해소할 수 있다.
- 모델 입력에는 저장 출처를 받지 않고 Desktop이 신뢰한 `conversationId`를 `conversation:<id>`로 주입한다.
- 관리 목록은 저장소 offset paging을 끝까지 순회하며, 표시 시각은 PC의 현재 시간대가 아니라 각 예약의 Windows time zone ID를 따른다.
- Windows 알림 API 실패와 제한 시간 초과는 해당 run의 `notification_error`로 격리하며 scheduler와 기억·대화 영속화는 계속 동작한다.
