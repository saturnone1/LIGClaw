# ADR 0019 — 격리 Edge 접근성 snapshot

## 결정

Protocol 1.9는 `browser.open.v1`과 `browser.snapshot.v1`을 추가한다. `browser.open`은 Desktop R3 승인 후 설치된 Microsoft Edge 실행 파일만 고정 위치에서 찾아 실행한다. 프로세스 인자는 LIGClaw 전용 `LocalAppData/LIGClaw/Browser/EdgeProfile`, `Default` profile, no-first-run, sync 비활성, 새 창과 검증된 HTTP(S) URL로 제한한다. shell 문자열이나 임의 실행 파일·인자를 받지 않는다.

Desktop은 실행 전후의 top-level `msedge` 창을 비교하고 새 창의 Win32 handle, PID, 프로세스 시작 시각을 UI Automation으로 확인한 뒤 30분 만료 browser handle을 발급한다. `browser.snapshot`은 R1 승인 후 handle과 프로세스 identity를 다시 검증하고 기존 bounded UIA 탐색을 사용해 최대 50개 요소의 이름·control type·활성·화면 밖 상태만 반환한다.

snapshot 결과에는 UIA element ID, Invoke/Value capability나 입력값을 포함하지 않는다. 따라서 이 계약만으로 클릭, 입력, 다운로드, 업로드 또는 인증을 수행할 수 없다. 페이지와 접근성 텍스트는 신뢰할 수 없는 외부 콘텐츠로 표시한다.

## 이유

- 개인 Edge profile의 쿠키·방문 기록·확장과 자동화 세션을 분리한다.
- 모델이 임의 browser 실행 인자나 실행 파일을 선택하지 못하게 한다.
- 재사용되거나 교체된 HWND를 PID와 프로세스 시작 시각으로 구분한다.
- 읽기 snapshot과 향후 쓰기 동작의 승인·권한 계약을 분리한다.

## 결과

- LIGClaw가 이번 프로세스에서 연 전용 Edge 창만 browser handle 대상이 된다.
- 창이 닫히거나 identity가 달라지거나 30분이 지나면 handle은 즉시 무효다.
- 개인 Edge 세션은 읽지 않으며 LIGClaw 전용 profile 데이터만 별도 디렉터리에 유지된다.
- 다운로드·업로드·인증 입력이 필요하면 별도 고위험 계약과 승인이 선행되어야 한다.
