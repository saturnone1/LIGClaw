# Windows 에이전트 기능 감사

> 기준일: 2026-08-02
> 기준: `docs/IMPLEMENTATION_PLAN.md`의 로컬 우선 Windows 개인 비서 MVP와 안전 정책

## 판정 기준

- **완료**: 모델 → Sidecar → Desktop 정책 → Windows adapter → 구조화 결과 경로와 자동 테스트가 있다.
- **부분**: 기본 경로는 있으나 사용자가 기대하는 하위 기능 또는 실기 acceptance가 남았다.
- **계획**: 구현 계획에 있으나 아직 Tool 계약과 수직 테스트가 없다.
- **후속**: MVP 밖이거나 별도 권한·개인정보·장치 호환성 설계가 먼저 필요하다.

## 현재 기능과 누락

| 영역 | 상태 | 현재 제공 | 남은 작업과 기본 위험도 |
|---|---|---|---|
| 시스템 기본 상태 | 완료 | Windows 릴리스·빌드·아키텍처·시간대·전원 | Protocol 1.14 배터리 잔량·충전·저전력/위험·에너지 절약 상태(R0), 실기기 matrix 대기 |
| 저장소 | 완료 | 논리 볼륨 용량·사용률, 물리 디스크 media/health, BitLocker 보호·암호화 상태. provider별 실패는 `unavailable`로 격리 | 제조사별 SMART 원시 속성은 후속 |
| CPU·메모리 | 완료 | CPU 사용률·논리 프로세서·메모리 전체/여유/사용률·업타임, Protocol 1.15 CPU·메모리 상위 앱 그룹(R1 매회 승인) | GPU/NPU·온도 |
| 네트워크 | 부분 | 네트워크 사용 가능 여부·어댑터 상태·속도(R0), Protocol 1.16 현재 IP/prefix·DNS·게이트웨이·연결 SSID(R1 매회 승인, 위치 권한 격리) | 연결 변경(R2), 실기기 위치 권한 matrix |
| 앱·창 | 부분 | 보이는 창 목록·설치 앱 검색·등록 앱 실행·창 활성화·정상 닫기·최소화·최대화·복원 | 가상 데스크톱은 후속(R2) |
| Explorer 문맥 | 완료 | 가장 최근 활성 Explorer의 현재 폴더·선택 항목을 승인 후 최대 20개 조회 | 탭별 문맥과 선택 변경 감지는 후속 |
| 파일 | 완료 | 검색·메타데이터·제한 텍스트 읽기·열기·복사·이동·이름 변경·휴지통·undo, 폴더 생성, bounded UTF-8 작성, ZIP 생성·안전 해제 | 덮어쓰기와 임의 archive 형식은 의도적으로 미제공 |
| 클립보드 | 완료 | 승인 기반 텍스트 읽기·쓰기 | 이미지/파일 형식과 변화 감지(후속) |
| 알림·예약 | 완료 | 즉시 알림, durable 단발·매일·매주 반복, lease/run 이력, 재시작 복구, DST·`skip`/`run_once_on_resume`/`ask`, 관리 화면 조회·수정·삭제 | 로그인 전·앱 미실행 실행이 필요하면 Task Scheduler adapter를 후속 검토 |
| 기억·선호 | 완료 | 승인 기반 명시적 remember/list/forget, 관리 화면 CRUD·JSON 내보내기, 출처·민감도·만료, 제한 Tool 인자의 별칭·선호 해석 | 암호화 내보내기와 만료 항목 영구 정리는 후속 |
| UI Automation | 부분 | 승인 기반 bounded inspect/find, Desktop 발급 element handle, identity/focus guard가 적용된 invoke/set-value/plain-text 입력, 비밀번호·사용자 입력 감지 차단 | 실제 다중 모니터·DPI와 앱별 provider 호환성 확대, 좌표 fallback은 별도 capability/승인 후속 |
| 프로세스·서비스 | 부분 | PID·경로·사용자명 없는 bounded 프로세스 이름별 CPU·메모리 관측(R1), 창 단위 정상 닫기 | 서비스 상태, 강제 종료/서비스 변경은 별도 R2~R4 분리 |
| Windows 설정·세션 | 부분 | allowlist Windows 설정 페이지 열기(R1), 잠금·절전(R2) | 로그아웃·재시작·종료는 별도 R3 설계 전까지 미제공 |
| 보안·업데이트 | 완료 | Defender 실시간 보호·서명 나이, 방화벽 profile, Windows Update 최근 성공, BitLocker 읽기(R0) | 보안 설정 변경은 별도 R3 후속 |
| 장치 | 부분 | 장치명·ID 없이 기본 오디오 출력, 활성 디스플레이 수·주 화면 해상도, 프린터 구성·기본 프린터 오프라인 상태(R0) | 실제 장치 구성 acceptance, Bluetooth·카메라와 제한 조작은 별도 개인정보·R1/R2 설계 후속 |
| 화면·OCR·음성 | 부분 | ADR 0033 개인정보 경계, composer 사용자 동작 기반 Windows picker 단일 프레임, D3D11/WARP, preview 드래그 crop·OCR 재실행·전체 복원, package identity 기반 로컬 OCR, 4K 비율 축소, `PreparedSensitiveContext` 상한·identity·전달 뒤에도 유지되는 2분 만료·zeroing, 선택 마스킹·취소 기본값·확인 텍스트만 합류 | 설치 실행과 Windows 10/11 실기 matrix, opt-in STT/TTS |
| MCP·외부 시스템 | 완료 | Streamable HTTP/Bearer 인증, 고정 실행 ID stdio, 연결별 health·장애 격리, exact-name allowlist, 읽기 전용 샘플 RAG | 외부 서비스 없이도 로컬 경로가 동작하며 사용자가 구성한 도달 가능한 endpoint는 주소 대역으로 차단하지 않음 |
| 배포·진단 | 부분 | self-contained x64 MSIX, 번들 Node, 서명·App Installer 자동화, 민감정보 제거 진단 번들, Sidecar/MCP soak | machine-trusted production 서명으로 깨끗한 Windows 계정 설치·업데이트·제거 gate |

## 실행 순서

1. **관측 기반 보강 — 완료**: 저장소·리소스·네트워크 R0 Tool과 실제 Windows snapshot 테스트.
2. **오류 경계 보강 — 완료**: 공급자 원문을 IPC/SQLite/UI에 전달하지 않고 안전한 오류 코드만 보존.
3. **사용자 문맥 완성 — 완료**: Explorer 선택 항목, 설치 앱 검색, 파일 메타데이터와 제한 텍스트 읽기.
4. **Phase 3 — 완료**: 명시적 기억, 로컬 별칭·선호 해석과 durable scheduler.
5. **Phase 4 — 기반 완료**: identity/focus guard를 갖춘 제한 UI Automation. 다중 모니터·DPI 실기 acceptance 진행.
6. **Phase 5 — 구현 완료**: MCP, 배포 자동화, 진단, 장시간 장애 검증. 신뢰된 서명 체인의 외부 설치 gate만 별도 추적.
7. **B단계 — 구현 완료**: 저장소·보안 관측, 파일 생산성, 창·설정·세션 제어, 대화 범위 승인, Agent 작업 제어와 데이터 백업·복원을 typed Tool/Desktop-owned 경계로 구현.
8. **C단계 — 진행 중**: 검증 기준선 복구, 책임 분리, 재현 가능한 release candidate, Windows 10/11·서명·실앱 UIA gate를 먼저 닫고 전원·프로세스·네트워크·장치 진단을 후속 수직 기능으로 추가.

범용 PowerShell/명령 프롬프트는 누락 기능을 메우는 우회로로 사용하지 않는다. 새 기능은 안정적인 버전 Tool, 최소 데이터 결과, Desktop canonical 위험도, replay/adapter 테스트를 함께 추가한다.
