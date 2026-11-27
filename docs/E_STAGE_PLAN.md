# LIGClaw E단계 계획 — 기능 카드 플랫폼 출시 안정화

> 상태: 자동 인수 및 unsigned MSIX 설치 산출물 완료
> 기준일: 2026-08-12
> 제품 핵심: 비개발자가 자연어로 기능 카드를 만들고, 안전 검토 후 바로 사용할 수 있는 로컬 우선 대시보드
> 기준 구현: D-0~D-7 인수 완료, Protocol 1.24, SQLite schema 21

## 1. 이번 목표

이번 단계는 개별 메일 기능을 더 만드는 단계가 아니다. 메일은 범용 Capability 플랫폼을 검증한 기준 사례로 유지한다. 출시 후보의 중심은 다음 한 문장이다.

> 사용자가 원하는 일을 설명하면 LIGClaw가 실행 가능한 범위 안에서 기능 카드를 제안하고, 검사·연결·권한·버전을 사용자가 확인한 뒤 활성화하여 반복 사용할 수 있다.

카드 한 장은 단순 바로가기가 아니라 정확한 Capability 버전, 선언형 Workflow, 필요한 Tool과 Connection, 최대 위험도, 활성 상태 및 rollback 이력을 가리킨다. 모델은 카드를 직접 설치하거나 Desktop 정책을 우회하지 않는다.

## 2. 출시 후보 완료 흐름

```text
기능 카드 대시보드
 → 새 기능 만들기 또는 대화의 “기능으로 만들기”
 → 자연어 요구와 필요한 경우 1~3개 추가 질문
 → Sidecar의 비영속 제안
 → Desktop 정적 검사와 무효과 dry-run
 → 검토 초안 생성
 → 필요한 Connection 선택·준비 상태 확인
 → 사용자의 명시적 활성화
 → 대화에서 exact Capability version 선택
 → Desktop 정책·승인 파이프라인으로 실행
 → 활동 및 Workflow 상태 확인
 → 새 버전 검토 또는 이전 활성 버전 rollback
```

## 3. E-0 — 기준선과 Git 보존

- 원격 `agent/phase1-desktop-foundation`은 37개 커밋이 강제 재작성됐지만 로컬과 원격의 파일 트리 및 patch-id는 동일하다.
- 로컬의 D단계 통합 변경은 pull/rebase로 덮어쓰지 않고 한 작업 트리에서 검증한다.
- 계약 생성물, SQLite schema, release manifest와 문서 상태를 함께 검사한다.

완료 조건:

- 로컬/원격에 실제 파일 차이가 없음이 기록됨
- `git diff --check`, 계약 생성 parity 및 전체 검증 통과
- 릴리스 산출물에서 비밀값·사용자 데이터 제외

## 4. E-1 — 범용 기능 카드 대시보드

- Capabilities 화면을 상태·버전·위험·출처·무결성을 한눈에 확인하는 native WPF 카드 대시보드로 제공한다.
- `새 기능 만들기`, `.ligcap` 가져오기, 검토, 활성화, 내보내기와 rollback을 같은 탐색 경계에 둔다.
- 빈 화면은 비개발자가 다음 행동을 이해할 수 있는 설명을 제공한다.
- 760×500에서는 카드 한 열, 넓은 화면에서는 여러 카드가 자연스럽게 배치되며 가로 스크롤을 만들지 않는다.
- keyboard focus, UI Automation 이름, high contrast용 동적 리소스 사용을 유지한다.

완료 조건:

- Builder 수직 테스트가 요구→질문→검사→초안→활성화→실행→활동 기록을 통과
- 카드 UI construction/XAML 회귀와 실제 UI smoke 통과
- 카드 생성만으로 활성화되거나 외부 효과가 발생하지 않음

## 5. E-2 — 보안·신뢰성 안정화

- Cline과 MCP SDK는 검증된 exact version을 유지한다.
- 실행 그래프에 들어오는 취약한 transitive dependency는 호환 가능한 최소 override로 고정한다.
- 패키지 설치 registry와 advisory registry를 분리해, audit API가 없는 사내 Nexus에서도 승인된 audit endpoint를 명시할 수 있게 한다.
- moderate 이상 advisory, 계약 drift, 빌드 경고, 테스트 실패는 릴리스를 차단한다.
- 미사용 Dify provider의 패치 불가능한 low advisory는 실행 노출과 upstream 상태를 계속 추적한다.

## 6. E-3 — 설치파일과 인수

- `scripts/build-release-candidate.ps1`로 self-contained x64 Desktop, bundled Node Sidecar, 계약·lockfile, manifest와 release evidence를 한 번에 생성한다.
- 산출물 SHA-256과 release manifest를 함께 제공한다.
- 개발/사내 배포용 unsigned MSIX는 생성할 수 있지만 일반 설치를 위해서는 Publisher와 일치하는 신뢰된 인증서 서명이 필요하다.
- 서명 인증서가 있으면 배포 단계에서 서명·설치→실행→업데이트→제거를 추가 검증한다. 현재 개발·인수 목표는 검증된 unsigned MSIX 산출물 생성까지이며, 선택적인 production 서명이 이 목표를 차단하지 않는다.

## 7. 이번 릴리스에서 하지 않는 일

- D-6C3/4 대용량 첨부 snapshot/MIME 전송
- D-8의 사내 시스템별 기본 Capability pack 확대
- D-9 자동 개선 제안과 사내 Git 배포
- D-10 arbitrary script, extension worker, Remote Runner
- public marketplace, WebView/HTML widget dashboard

위 항목은 카드 플랫폼과 설치 기준선이 안정된 뒤 독립 수직 슬라이스로 진행한다. 특히 이메일은 범용 기능 추가 구조의 사례이지 제품 범위를 이메일 클라이언트로 제한하는 근거가 아니다.

## 8. 최종 인수 조건

- Sidecar 141개와 모든 .NET 테스트가 순차 환경에서 통과
- `./scripts/verify.ps1` exit 0, build warning/error 0
- `./scripts/smoke-ui.ps1`의 일반·760×500 UIA 검사 통과
- moderate/high npm advisory 0, 잔여 low advisory와 비노출 근거 기록
- release candidate MSIX, SHA256SUMS, release manifest/evidence 생성
- 문서의 구현·검증 수치와 실제 결과 일치
