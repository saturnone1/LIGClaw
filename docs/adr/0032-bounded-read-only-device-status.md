# ADR 0032: 식별자를 제외한 읽기 전용 장치 상태

- 상태: 승인
- 날짜: 2026-08-01

## 배경

일반 사용자는 소리가 나지 않거나 화면·프린터가 준비되지 않은 상황을 Agent에게 물을 수 있어야 한다. 반면 장치명, 하드웨어 ID, 드라이버, 프린터 포트는 진단에 항상 필요하지 않고 사용자·조직 환경을 식별할 수 있다. 장치 변경은 읽기보다 실패 영향과 권한 위험이 크므로 같은 Tool에 포함하면 안 된다.

## 결정

Protocol 1.17에 R0 `system.get_device_status.v1`을 추가한다. 입력은 없으며 다음 최소 상태만 반환한다.

- 기본 render 오디오 endpoint의 `active`, `disabled`, `not_present`, `unplugged`, `unknown`
- 활성 디스플레이 수 최대 16개, 주 디스플레이가 있으면 가로·세로 픽셀
- 관측된 프린터 수 최대 32개, 기본 프린터의 `online`, `offline`, `not_configured`, `unknown`

장치명, endpoint ID, 모니터 이름, 하드웨어 ID, 드라이버, 프린터 이름·포트는 읽어 결과에 넣거나 감사 로그에 저장하지 않는다. 오디오, 디스플레이, 프린터 provider 상태를 각각 분리해 한 provider의 실패가 나머지 결과를 없애지 않게 한다. 모든 호출은 UI thread 밖에서 수행하고 Desktop Tool timeout과 취소 경계를 따른다.

오디오는 Windows Core Audio의 `IMMDeviceEnumerator::GetDefaultAudioEndpoint`와 `IMMDevice::GetState`를 사용한다. 이 API는 Windows Vista부터 지원되므로 Windows 10/11 공통 adapter로 유지한다. 디스플레이는 Windows Forms가 제공하는 현재 화면 열거 결과에서 이름을 접근하지 않고 수와 주 화면 크기만 사용한다. 기반 Win32 `EnumDisplayMonitors`와 `MONITORINFO`도 Windows 10/11보다 오래된 공통 API다. 프린터는 기존 고정 WMI client로 `Win32_Printer.Default`와 `WorkOffline`만 조회한다.

- [IMMDeviceEnumerator::GetDefaultAudioEndpoint](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint)
- [IMMDevice::GetState](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getstate)
- [EnumDisplayMonitors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)
- [MONITORINFO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-monitorinfo)
- [Win32_Printer](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-printer)

## 결과와 제한

- 현재 상태 확인은 승인 팝업 없이 사용할 수 있지만 개인화된 장치 식별 정보는 얻을 수 없다.
- 프린터 driver가 spooler에 정확한 상태를 제공하지 않을 수 있으므로 알 수 없는 값은 추측하지 않고 `unknown`으로 반환한다.
- 기본 장치 변경, 볼륨 조절, 디스플레이 설정 변경, 인쇄 취소는 이 Tool의 범위가 아니다. 필요하면 별도 입력 계약과 R1/R2 정책으로 추가한다.
- Bluetooth·카메라는 개인정보·권한 경계를 별도 검토할 때까지 포함하지 않는다.
