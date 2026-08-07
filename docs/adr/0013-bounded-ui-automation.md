# ADR 0013 — 제한된 Desktop 소유 UI Automation

## 결정

UI Automation 조회와 조작은 Desktop 프로세스만 수행하며 Sidecar에는 `uia.inspect.v1`, `uia.invoke.v1`, `uia.set_value.v1`, `uia.send_text.v1`의 schema-first 계약만 노출한다. `inspect`는 화면에 표시된 이름·AutomationId·control type·지원 pattern만 최대 50개 반환하고 현재 Value 원문은 읽지 않는다. 화면 내용이 모델로 전달될 수 있으므로 R1 승인 대상으로 둔다. 나머지 조작은 R2이며 매번 실제 값과 대상을 승인받는다.

`elementId`는 Desktop이 무작위로 발급하는 5분짜리 handle이다. handle은 HWND, PID, 프로세스 시작 시각, UIA runtime ID, AutomationId, control type과 이름을 묶는다. 승인 직전과 실행 직전에 동일 identity를 다시 해석하며 하나라도 달라지면 실행하지 않는다. 최대 1,500개 요소만 탐색하고 handle cache는 500개로 제한한다.

조작 직전 대상 최상위 창을 foreground로 전환하고 다시 확인한다. 값 설정과 텍스트 입력은 대상 요소 또는 그 하위 UIA peer에 실제 keyboard focus가 있는지도 확인한다. `send_text`는 Edit 요소에 최대 500자의 일반 Unicode 문자만 `SendInput`으로 전달하며 focus 안정화 구간에 사용자 입력이 감지되면 중단한다. modifier, 단축키, 좌표 클릭은 이 capability에 포함하지 않는다.

비밀번호 요소는 inspect 결과에서 값 설정·텍스트 입력 지원을 숨기고 Desktop adapter에서도 승인과 실행을 모두 거부한다. 입력 payload는 승인 상세에는 표시하지만 감사 summary와 진단에는 기록하지 않는다.

## 이유

- HWND와 화면 좌표만으로 조작하면 창 이동, DPI, 다중 모니터와 HWND 재사용에서 잘못된 대상을 건드릴 수 있다.
- UIA runtime identity와 프로세스 시작 시각을 함께 확인하면 같은 제목의 다른 창으로 동작이 이동하는 것을 막을 수 있다.
- 읽기와 쓰기의 민감도를 분리하고 비밀번호·임의 키 입력을 제외해 기본 Windows 에이전트의 권한을 좁게 유지한다.
- 좌표에 의존하지 않아 창 이동과 DPI 배율 변화가 element identity를 바꾸지 않는다.

## 결과

- Windows 10 1809 이상 공통 `UiAutomation` capability로 등록한다.
- WPF와 native Win32 control fixture에서 창 이동 후 inspect와 handle 재해석을 자동 검증한다.
- 앱별 UIA provider가 멈출 수 있으므로 호출은 10초 경계로 격리한다. 중단할 수 없는 외부 provider 호출 thread가 남을 가능성은 후속 out-of-process UIA broker 검토 대상이다.
- 실제 다중 모니터·서로 다른 DPI 화면 간 이동과 사용자 입력 경쟁 acceptance는 해당 장치가 있는 실기 환경에서 계속 검증한다.
