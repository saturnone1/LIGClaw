# ADR 0027: Desktop 설정과 비밀의 원자적 저장

## 결정

레지스트리 metadata와 Windows Credential Manager 비밀을 함께 변경하는 Desktop 설정 저장은 이전 metadata와 비밀을 먼저 읽고, 어느 단계에서든 실패하면 모든 이전 값을 복구한다. 복구 단계 하나가 실패해도 나머지 복구는 계속 시도하며 원래 저장 오류와 복구 오류를 함께 반환한다. prompt, API key, token 값은 오류 메시지나 로그에 포함하지 않는다.

모델 프로필과 MCP 저장소의 기존 rollback 경계를 유지하고, 의미 기억 설정도 `AtomicSettingsMutation`을 사용해 같은 실패 의미를 갖도록 한다. section controller는 검증과 호출 순서를 소유하지만 Credential Manager와 레지스트리 효과 및 rollback은 Desktop store만 소유한다.

## 이유

metadata만 새 값이고 비밀은 이전 값이거나 그 반대인 부분 저장은 다음 실행에서 잘못된 endpoint에 비밀을 전송하거나 연결을 복구할 수 없게 만든다. 모든 복구 단계를 실행하는 결정적 helper는 실패 주입 테스트를 가능하게 하면서 비밀 저장 구현을 Sidecar와 Application 경계 밖에 유지한다.

## 결과

- 설정 저장 성공 시 metadata와 비밀이 같은 버전의 입력을 나타낸다.
- 저장 실패 시 가능한 모든 이전 값을 복구하며 부분 복구 실패를 숨기지 않는다.
- 비밀 값은 SQLite, Sidecar 설정 파일, 진단 번들 또는 로그로 이동하지 않는다.
