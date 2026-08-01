# LIGClaw UI/UX acceptance

> 기준일: 2026-08-02
> 상태: 완료 — 자동 검증과 현재 장비 실기 검증 통과

## 검증 결과

| 영역 | 결과 | 증거 |
|---|---|---|
| 통합 탐색 | 통과 | UI Automation smoke에서 홈과 설정·실행 활동·기억·예약·Agent 작업을 한 top-level window 안에서 전환 |
| 키보드 탐색 | 통과 | 모든 주 탐색 버튼의 keyboard focus와 홈 복귀 후 입력 포커스 복원 확인 |
| 최소 창 크기 | 통과 | 760×500에서 대화와 설정·활동·기억·예약의 핵심 입력·필터·정렬·목록 노출 확인 |
| 설정 오류 | 통과 | 잘못된 Base URL 입력 시 해당 필드 옆 inline validation 노출 확인 |
| 관리 화면 | 통과 | 활동 상태 필터, 기억 검색, 예약 상태 필터, 세 화면의 정렬 및 결과·빈 상태 피드백 확인 |
| 설정 연결 순서 | 통과 | 변경한 공급자 정보는 동일 입력의 연결 테스트가 성공하기 전 저장되지 않는 정책 테스트와 단계 표시 검사 |
| 접근성 | 통과 | 입력 컨트롤 accessible name, live region, 명시적 focus visual, high-contrast palette 회귀 테스트 |
| 동작 감소 | 통과 | Storyboard와 DoubleAnimation 미사용 회귀 테스트 |
| modal 안정성 | 통과 | 승인 창을 STA에서 생성·측정하는 결정적 회귀 테스트 |
| 긴 텍스트·대량 데이터 | 통과 | 긴 한국어 설명을 포함한 실행 1,000개를 588×500 콘텐츠 영역에서 구성하고 recycling virtualization 확인 |
| Markdown 링크 | 통과 | HTTP(S) 링크를 자동 실행하지 않고 라벨과 선택·복사 가능한 전체 주소로 렌더링 |
| UI 이벤트 예외 | 통과 | 복구 가능한 Dispatcher 예외는 대화 화면의 재시도 안내로 전환하고 치명적 런타임 예외는 숨기지 않는 정책 검사 |
| DPI 설정 | 통과 | Desktop 프로젝트 `PerMonitorV2` 설정과 760 DIP 반응형 정책 테스트 |
| 앱 오류 | 통과 | UI Automation smoke 수행 중 Windows Application event 오류 0건 |
| 실제 화면 | 통과 | 1060×650 홈 및 실행 활동 화면에서 겹침·잘림·상태 표현을 앱 창 단위로 확인 |
| 상태·진단 정보 | 통과 | 대화 본문을 전체 폭으로 사용하고 도우미 상태는 하단 한 줄로 축소, 문제 해결 로그는 크기 조절·전체 복사가 가능한 별도 창으로 제공 |
| 대화 연속성 | 통과 | 같은 대화의 후속 요청은 `conversationId`를 유지하고 매 요청의 `runId`만 교체하는 상태 테스트와 Sidecar replay 테스트 |
| 대화 격리 | 통과 | `새 대화`를 누르거나 다른 최근 대화를 선택할 때만 문맥이 분리되고 서로 다른 대화의 메시지가 섞이지 않는 회귀 테스트 |
| 재시작 복원 | 통과 | Desktop SQLite의 완료된 최근 20턴을 bounded history로 복원하고 실패 턴은 모델 문맥에서 제외하는 저장소·통합 테스트 |
| Precision Workspace 셸 | 통과 | 72/216px adaptive AppRail, command header의 대화 전환기, 단일 정상 상태 표시와 880px 읽기 폭을 정책·XAML·실기 smoke로 확인 |
| 대화 작업 공간 | 통과 | 빈 대화의 hero composer와 안전한 예시, 대화 시작 후 docked composer, Action Card, 수동 스크롤 시 새 응답 indicator 동작 확인 |
| 관리·승인 화면 | 통과 | 활동·기억·예약의 공용 command bar/data surface와 설정 섹션, 행동 우선 승인 요약 및 항상 허용 범위 표시 확인 |
| 민감 화면 preview | 통과 | 전용 창에서 이미지·OCR 동시 검토, 선택 마스킹·전체 삭제, 취소 기본 포커스, 잘못된 이미지·32KiB 초과 차단, 닫기 시 원문 zeroing을 STA·policy 테스트로 확인 |
| 화면 가져오기 | 자동 통과·실기 대기 | composer의 접근 가능한 명시 버튼, Windows picker 취소/미지원/실패 분리, 드래그 crop의 원본 픽셀 매핑·재인식·전체 복원, 4K OCR 비율 축소, Unicode-safe 제한과 전달 뒤 2분 buffer zeroing을 정책·STA·XAML로 확인. 설치 실행 OCR과 Windows 10/11 다중 모니터는 외부 gate |
| 누르고 말하기 | 자동 통과·설치 실기 대기 | 설정의 기본 꺼짐 opt-in, 접근 가능한 composer 버튼, 마우스·Space/Enter press/release, 시작 중 release·중복 종료·취소 경합, 60초 timeout, 16,000자 Unicode 경계와 microphone manifest를 테스트로 확인. 설치 상태 권한 prompt와 Windows 10/11 실제 인식은 외부 gate |
| 직접 스타일 부채 | 통과 | view XAML의 직접 숫자 FontSize 0건, 문자 glyph 기능 아이콘 0건, 직접 HEX 0건을 회귀 검사로 고정 |
| 요청 취소 안정성 | 통과 | 사용자 취소·Sidecar 단절·run 종료가 동일 run의 Desktop Tool cancellation token으로 전파되고 다른 run에는 누출되지 않는 회귀 테스트 |
| Tool 동시성 | 통과 | Desktop Tool 승인과 실행을 직렬화해 병렬 요청의 modal 중첩과 Windows 효과 경합 방지 |
| 긴 응답 렌더 성능 | 통과 | transcript 길이에 따라 Markdown 렌더 주기를 제한하고 실제 렌더 후 독자 위치를 기준으로 새 응답 indicator 판정 |
| 대화 원문 검색 | 통과 | SQLite trigram FTS로 사용자·도우미 턴을 검색하고 180ms debounce, stale-result 차단, bounded query와 접근 가능한 검색 상태 제공 |

## 실행한 검증

- `./scripts/smoke-ui.ps1`: 통합 관리 페이지 5개, top-level window 1개, 모든 페이지의 760×500 compact layout, field validation, 관리 화면 정렬, 명시적 새 대화, keyboard navigation, 포커스 복원, Precision Workspace hero 예시, Application 오류 0건 모두 통과
- `./scripts/verify.ps1`: Sidecar 84, Contracts 7, Application 20, Desktop 358, Sidecar integration 1 테스트 통과; 빌드 경고 0, 오류 0
- 화면 가져오기 집중 검증: 화면·OCR·crop·전달 후 만료·민감 컨텍스트·XAML 46개 통과. 실행 중 사용자 앱을 유지한 격리 출력 전체 verify도 통과
- `dotnet format LIGClaw.slnx --no-restore`: 통과

## 디스플레이 검증 범위

사용자 결정에 따라 150%·200% 배율과 물리 다중 모니터 실기 검증은 이 목표의 필수 완료 조건에서 제외한다. 현재 장비의 100% 배율, 760×500 compact UI Automation, `PerMonitorV2` 설정 및 반응형 정책 테스트를 유지하되 다른 장비 확보를 기다리며 UI/UX 목표를 열어 두지 않는다.

## 알려진 비차단 항목

`npm audit`의 low advisory 1건은 실행 경로에서 사용하지 않는 Dify 하위 의존성의 `@ai-sdk/provider-utils`에서 발생한다. 강제 major override는 적용하지 않고 의존성 갱신 항목으로 추적한다.
