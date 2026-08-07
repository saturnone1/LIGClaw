# ADR 0021: 실행별 불변 모델 프로필 routing

## 결정

모델 endpoint·model·Credential Manager secret은 Desktop의 이름 있는 프로필로 저장한다. 기본 프로필과 최대 4개 fallback 순서는 Desktop 설정이 소유하며, 실행 시작 시 API key를 포함한 routing snapshot을 인증된 named pipe로 Sidecar에 한 번 전달한다. snapshot과 secret은 로그·진단 번들·SQLite에 저장하지 않는다.

Sidecar는 fallback이 설정된 실행을 시작하기 전에 bounded OpenAI-compatible probe로 순서를 확인한다. fallback은 인증 실패, 모델 부재, rate limit, timeout·network·5xx 일시 장애에만 허용하며 `routing_changed` 이벤트에는 프로필 ID와 고정 사유 코드만 싣는다. 명시적으로 선택한 프로필은 fallback하지 않는다. 동일 대화의 routing key가 endpoint·model·profile·secret hash 중 하나라도 달라지면 cached runtime session을 교체한다.

## 결과

- 동시 실행은 설정 변경으로 provider가 뒤섞이지 않는다.
- API key는 프로필별 Windows Credential Manager에 분리된다.
- 잘못된 요청이나 권한 거부 같은 비대상 오류는 다른 모델로 숨기지 않는다.
- fallback probe를 사용하지 않는 단일 프로필 경로에는 추가 연결 테스트를 강제하지 않는다.
