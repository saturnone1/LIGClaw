# LIGClaw B단계 Windows 실무 자동화 계획

> 상태: 완료 (2026-07-26)  
> 기준일: 2026-07-26  
> 전제: 에어갭·사내망 Windows 사용자 세션에서 임의 shell 없이 typed Tool과 Desktop 정책 경계로 동작

## 목표

A단계의 탐색·기억·예약·모델 복구·로컬 병렬 작업 기반 위에 Windows 실무 자동화를 확장한다. 읽기와 변경을 별도 계약으로 나누고, 관리자 권한이 없거나 특정 Windows provider가 비활성인 장치에서도 전체 요청을 실패시키지 않고 항목별 `unavailable` 상태를 반환한다.

## 단계

1. **B-1 저장소·보안 관측**
   - 물리 디스크 health와 BitLocker 보호 상태
   - Defender, Windows 방화벽, Windows Update 최근 성공 상태
   - exact WMI/COM provider만 읽고 명령줄·PowerShell은 사용하지 않음
2. **B-2 파일 생산성**
   - 승인 기반 폴더 생성, bounded UTF-8 파일 작성, ZIP 압축·해제
   - canonical/reparse 재검증, 덮어쓰기 금지, 크기·항목 수 제한, 가능한 작업의 undo
3. **B-3 창·설정·세션 제어**
   - 창 최소화·최대화·복원, allowlist Windows 설정 페이지 열기
   - 잠금·절전은 별도 R2 승인, 로그아웃·재시작·종료는 범위 밖
4. **B-4 승인·Agent 작업 UX**
   - 정확한 scope의 `이 대화 동안` 승인과 철회
   - Agent 작업 일시정지·재개·즉시 재시도, 완료 알림과 결과로 이동
5. **B-5 운영 안정화**
   - 장시간 실행, 절전 복귀, 모델 장애 fault test
   - 원자적 데이터 백업·검증된 복원, 보존 기간 기반 정리 정책

## 공통 완료 조건

- 모든 모델 진입점은 schema-first 계약과 Sidecar replay 테스트를 가진다.
- Desktop만 Windows 효과, 승인, SQLite와 백업 파일을 소유한다.
- R2 이상은 매번 영향 범위를 보여주고 명시적으로 승인받는다.
- API 키, 프롬프트, 파일·클립보드 원문을 로그에 기록하지 않는다.
- `./scripts/verify.ps1`, Sidecar smoke와 UI smoke를 모두 통과한다.

## 완료 증거

- Protocol 1.13 schema 생성 일치 및 Sidecar deterministic Tool replay 78개 통과
- Contracts 7개, Application 20개, Desktop 248개, Named Pipe 통합 1개 통과
- Sidecar handshake·heartbeat·restart·cleanup smoke와 760×500 UI smoke 통과
- Desktop Sidecar 강제 재시작 10회 soak 통과: private memory +5,378,048 bytes, handle +44
- 복원 staging 변조 방지, ZIP traversal·symlink 차단, provider 장애 격리, 완료 알림 실패 격리 테스트 포함
