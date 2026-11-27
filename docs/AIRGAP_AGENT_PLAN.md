# LIGClaw 에어갭 에이전트 확장 계획

> 상태: 완료 — A-1~A-6 구현 및 전체 회귀·Sidecar/UI smoke 검증 통과  
> 기준일: 2026-07-26  
> 전제: 공용 인터넷, 외부 SaaS, 외부 메시징 채널 없이 Windows 사용자 세션과 승인된 사내망에서 동작

## 목표

LIGClaw의 Desktop 소유 권한·승인·감사 경계를 유지하면서 에어갭 환경에서도 가치가 있는 탐색, 기억, 자동화, 모델 복구, 병렬 작업 기능을 확장한다. 모든 핵심 기능은 외부 서비스 의존 없이 실행·복구·검증할 수 있어야 하지만, 앱이 현재 네트워크에서 도달 가능한 endpoint를 사내망·공인망 여부로 인위적으로 차단하지 않는다.

## 포함 범위

1. **사내망 브라우저 자동화와 안전한 search/fetch**
   - 현재 네트워크에서 도달 가능한 HTTP(S) endpoint 사용
   - URL scheme, redirect 횟수, 응답 크기, 콘텐츠 형식과 timeout을 제한하고 주소 대역 자체는 차단하지 않음
   - 설치된 Edge의 별도 프로필을 사용하고 개인 브라우저 프로필과 쿠키를 공유하지 않음
   - 접근성 snapshot 기반 탐색, 만료 handle, 다운로드·업로드·인증 입력 별도 승인
   - 검색 공급자는 로컬·사내 검색 endpoint를 기본으로 하되 사용자가 구성한 공급자를 허용

2. **대화 FTS 검색과 선택형 의미 기억**
   - Desktop SQLite FTS를 이용한 사용자·도우미 대화 원문 검색
   - 로컬 embedding endpoint를 사용자가 켠 경우에만 의미 기억 색인
   - 로컬 embedding을 기본으로 하며 원격 endpoint를 선택하면 전송 범위를 설정과 승인 화면에 명시
   - 자동 기억 저장은 기본 비활성, 출처·민감도·만료·갱신 시각 유지

3. **예약 agent job과 background task**
   - 알림 외에 읽기·분석·보고서 생성 agent job을 durable scheduler로 실행
   - 실행마다 독립 run ID, 최대 시간·반복 횟수·모델 프로필·결과 크기 제한
   - 파일 변경과 Windows 효과는 예약 생성 승인만으로 실행하지 않고 실행 시점 정책을 다시 통과
   - 재시작·절전 복귀·실패 재시도·부분 결과를 활동 이력에 보존

4. **다중 로컬 모델 프로필과 fallback**
   - 여러 OpenAI 호환 localhost/사내망 프로필, Credential Manager 비밀 분리
   - 작업별 primary/fallback 순서와 명시적인 전환 사유
   - 인증 실패, rate limit, 일시 장애, 모델 부재만 제한적으로 fallback
   - 사용자가 명시적으로 고른 모델은 암묵적으로 다른 모델로 전환하지 않음

5. **로컬 하위 에이전트와 백그라운드 작업**
   - Desktop이 parent/child task와 권한 상한을 소유
   - 하위 에이전트는 부모보다 넓은 Tool·MCP·네트워크 권한을 얻을 수 없음
   - Windows 효과와 외부 전송은 기존 Desktop 승인 파이프라인 하나로 직렬화
   - 취소 전파, 결과 합류, bounded concurrency, 프로세스 재시작 후 orphan 정리

## 제외 범위

- Skills·플러그인 마켓과 임의 코드 설치
- Telegram, Teams 등 외부 메시징 채널
- 화면 OCR/VLM, 카메라, 음성 입력·출력
- 공용 인터넷이나 cloud 서비스가 없으면 동작하지 않는 필수 의존성
- 임의 shell/PowerShell 실행

## 구현 순서

### A-1 — 대화 FTS 검색 — 완료

SQLite schema 8의 trigram FTS 색인과 insert/update/delete trigger, 사용자·도우미 턴 bounded 검색, 짧은 특수문자 literal 검색, 180ms UI debounce와 stale-result 차단, 대화 전환기 검색 UI를 구현했다.

### A-2 — 선택형 의미 기억 — 완료

설정에서 명시적으로 켜는 OpenAI 호환 embedding 프로필, Credential Manager API 키, SQLite schema 9 float32 벡터 저장, 내용 해시 기반 지연 재색인, keyword+cosine 결과 결합, 수정·삭제·만료 동기화를 구현했다. 주소 대역은 차단하지 않으며 요청·응답 크기와 벡터 구조를 검증하고 endpoint 장애 시 keyword 검색으로 복구한다. 상세 저장·전송 경계는 ADR 0017을 따른다.

### A-3 — 안전한 web 기반

**A-3.1 완료:** Protocol 1.8의 schema-first `web.fetch.v1`·`web.search.v1`, 매 요청 Desktop R3 승인·감사, 구성형 `{query}` 검색 endpoint, 쿠키 없는 GET, redirect 5회·본문 512KB·출력 60,000자·text/JSON/XML/HTML 제한과 HTML text 추출을 구현했다. 네트워크 주소 대역은 차단하지 않으며 최종 URL과 실제 응답 범위를 결과에 포함한다. 상세 경계는 ADR 0018을 따른다.

**A-3.2 완료:** Protocol 1.9의 `browser.open/snapshot`, LIGClaw 전용 Edge user-data-dir, 새 Edge 창 identity 확인, 30분 만료 browser handle과 최대 50개 접근성 요소 snapshot을 구현했다. snapshot에는 쓰기 가능한 element handle을 노출하지 않는다. 다운로드·업로드·인증 입력은 읽기 계약에서 제외했으며 향후 필요할 때 별도 고위험 계약으로 다룬다. 상세 경계는 ADR 0019를 따른다.

### A-4 — durable agent job

**A-4.1 완료:** SQLite schema 10의 독립 `agent_jobs`·`agent_job_runs`, 시간대·DST 반복, run ID·lease, 실행 시간·재시도·결과 크기 상한, 재시작 interrupted 복구와 최대 2개 bounded scheduler를 구현했다. 상세 경계는 ADR 0020을 따른다.

**완료:** Protocol 1.10의 schema-first create/list/cancel Tool, `Agent 작업` 관리 UI, 실제 Sidecar background executor와 bounded 결과 이력을 연결했다. `skip / 재개 시 1회 / 확인` misfire, lease 만료, 재시작 interrupted, 최대 실행·재시도·결과 크기를 durable ledger에서 처리한다. Windows 효과는 예약 승인과 분리되어 실행 시점 Desktop 정책을 다시 통과한다.

### A-5 — 모델 프로필·fallback

**완료:** Protocol 1.11의 immutable per-run routing snapshot, 프로필별 Credential Manager secret, 설정 UI의 기본/fallback 순서, 최대 4개 fallback을 구현했다. 인증 실패, 404 모델 부재, 429 요청 한도, timeout·network·5xx 일시 장애에서만 전환하고 프로필 ID와 고정 사유 코드를 남긴다. 사용자가 Agent job에서 프로필을 명시하면 fallback을 비활성화한다. 상세 경계는 ADR 0021을 따른다.

### A-6 — 하위 에이전트

**완료:** Protocol 1.12의 `subagent.run.v1`, SQLite schema 11 parent/child ledger, 최대 4개 task·동시 3개 child run, task별 시간·결과 크기와 R0/R1 권한 상한, 부모 취소 전파, 결과 합류, 재시작 orphan 정리를 구현했다. child는 추가 child를 만들 수 없고 모든 Windows Tool은 기존 Desktop 승인·감사 경계를 다시 통과한다. 상세 경계는 ADR 0022를 따른다.

## 공통 완료 조건

- 공용 인터넷 없이 전체 자동 테스트와 smoke가 통과한다.
- 외부 네트워크 없이도 핵심 기능과 로컬 공급자 경로가 동작하며, 연결 가능한 endpoint를 주소 대역만으로 차단하지 않는다.
- 프롬프트, 대화 원문, 기억 원문, API 키를 로그에 남기지 않는다.
- 새 프로토콜 버그마다 contract/replay 테스트가 있다.
- Desktop이 비밀, 저장소, 권한, 스케줄, Windows 효과를 계속 단독 소유한다.

## 최종 검증

- `./scripts/verify.ps1`: Sidecar 68, Contracts 7, Application 20, Desktop 227, Named Pipe 통합 1 테스트 통과; 빌드 경고·오류 0
- `./scripts/smoke-sidecar.ps1`: handshake, heartbeat, 강제 재시작, 고아 프로세스 정리 통과
- `./scripts/smoke-ui.ps1`: 통합 관리 페이지 5개, top-level window 1개, 760×500 compact layout, 포커스·검증·정렬·대화 경계·키보드 탐색 통과; Application 오류 0
- 비차단 항목: Dify 하위 의존성 `@ai-sdk/provider-utils`의 low severity advisory 1건은 강제 override 없이 의존성 갱신 대상으로 유지한다.
