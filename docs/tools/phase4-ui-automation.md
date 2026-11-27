# Phase 4 UI Automation Tool

## 제공 범위

| Tool | 위험도 | 동작 |
|---|---:|---|
| `uia.inspect.v1` | R1 | 승인한 최상위 창에서 UI 요소 metadata를 검색·조회 |
| `uia.invoke.v1` | R2 | InvokePattern을 지원하는 승인 요소 실행 |
| `uia.set_value.v1` | R2 | 비밀번호가 아닌 ValuePattern 요소 설정·비우기 |
| `uia.send_text.v1` | R2 | 비밀번호가 아닌 Edit 요소에 일반 Unicode 텍스트 입력 |

먼저 `app.list_windows.v1`에서 `windowId`를 얻고 `uia.inspect.v1`이 반환한 `elementId`를 조작 Tool에 사용한다. `elementId`는 5분 후 만료되며 앱 UI가 바뀌어 identity가 달라져도 사용할 수 없다.

## 안전 제한

- 조회 결과 최대 50개, 내부 탐색 최대 1,500개
- 현재 입력 Value와 비밀번호 원문은 조회하지 않음
- 비밀번호 요소의 값 설정과 텍스트 입력 금지
- HWND·PID·프로세스 시작 시각·UIA runtime identity 재검증
- foreground 창과 요소 focus가 맞지 않으면 실행 중단
- 텍스트 입력 안정화 중 사용자 입력이 감지되면 실행 중단
- modifier·단축키·좌표 클릭·이미지 기반 fallback 없음

좌표를 사용하지 않으므로 창 이동과 DPI 변화는 handle identity에 포함하지 않는다. UIA를 제공하지 않는 앱의 좌표 fallback은 별도 capability와 별도 승인 설계 전에는 추가하지 않는다.
