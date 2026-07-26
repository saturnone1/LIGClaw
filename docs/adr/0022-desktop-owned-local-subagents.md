# ADR 0022: Desktop 소유 로컬 하위 Agent

## 결정

`subagent.run.v1`은 한 번의 승인으로 제목·지시가 명시된 task 1~4개를 요청한다. Desktop은 SQLite schema 11의 batch/task ledger와 child run ID를 만들고 최대 3개만 병렬 실행한다. task별 실행 시간은 30~900초, 결과는 1,000~20,000자로 제한한다.

각 child는 별도 Sidecar conversation을 사용하지만 Desktop이 보관한 immutable model routing을 받는다. 권한 상한은 R0 또는 R1이고, child의 Tool 호출은 별도 `ToolInvocationPolicy`, 사용자 승인, 감사 저장을 다시 통과한다. 상한보다 높은 호출과 재귀 `subagent.run`은 Desktop에서 거부한다. 부모 취소는 대기·실행 child 모두에 전파하고, 재시작 시 남은 running ledger는 `interrupted`로 정리한다.

## 결과

- 하위 Agent는 부모 요청보다 넓은 Windows 권한을 얻지 못한다.
- child 결과와 고정 오류 코드는 부모 Tool 결과로 합쳐지고 durable ledger에도 남는다.
- 임의 shell, 플러그인 설치, 외부 메시징 채널은 추가하지 않는다.
