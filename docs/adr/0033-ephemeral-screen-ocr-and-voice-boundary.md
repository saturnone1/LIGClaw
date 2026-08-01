# ADR 0033: 화면·OCR·음성의 일회성 개인정보 경계

- 상태: 승인
- 날짜: 2026-08-01

## 구현 현황 (2026-08-02)

Desktop composer의 `화면 가져오기`가 WPF HWND에 연결한 `GraphicsCapturePicker`를 열고, 사용자가 고른 창·디스플레이에서 `CreateFreeThreaded` frame pool로 한 프레임만 가져온다. 하드웨어 D3D11 장치 생성이 실패하면 Windows WARP로 한 번 대체하며, 10초 frame timeout과 캡처 전·후 4,096px/16MP 검증, PNG 8MiB 검증을 적용한다. 취소·미지원·timeout·실패는 서로 다른 내부 상태이며 원문은 진단에 기록하지 않는다.

패키지 identity가 있는 실행에서는 Windows 로컬 OCR을 사용한다. 4K 같은 일반 화면을 OCR 엔진의 더 작은 입력 한도 때문에 거부하지 않도록 OCR용 bitmap만 종횡비를 유지해 축소하며 원본 preview는 유지한다. OCR 결과는 UTF-8 32KiB 경계에서 Unicode 문자를 자르지 않고 제한한다. 사용자가 preview에서 확인·마스킹한 텍스트만 composer에 추가하고 이미지 자체는 Agent나 Sidecar로 전달하지 않는다. unpackaged 실행은 설치 필요 안내로 끝나며 cloud fallback은 없다.

preview에서는 마우스 드래그 선택을 Uniform 표시 좌표에서 원본 픽셀로 변환하고, 선택 영역 PNG를 메모리에서 다시 만든 뒤 해당 영역에만 OCR을 재실행한다. 성공해야 이미지와 텍스트를 함께 교체하며 실패하면 기존 검토 내용을 유지한다. 전체 화면 복원도 제공한다. store에서 preview로 소유권이 전달된 뒤에도 컨텍스트 자체의 2분 타이머가 버퍼를 지우고 창을 닫는다.

남은 acceptance는 signed/unsigned MSIX 설치 실행에서 OCR 성공·언어팩 부재, Windows 10 22H2와 Windows 11의 picker 취소·다중 모니터·보호 콘텐츠다.

## 배경

화면과 음성은 비밀번호, 사내 문서, 개인정보, 알림 내용을 한 번에 포함할 수 있다. 기존 Tool 승인 창은 텍스트 동작 설명만 보여 주므로 실제 캡처 내용의 전송 전 확인·마스킹을 증명하지 못한다. 이 상태에서 모델 호출만으로 캡처를 시작하거나 원문을 Sidecar에 바로 넘기는 것은 기존 Desktop 정책 경계를 우회한다.

Windows의 화면 캡처 API는 사용자가 창 또는 디스플레이를 고르는 보안 시스템 UI와 캡처 표시를 제공한다. Windows OCR과 최신 음성 인식 API는 desktop app에서 package identity를 요구하므로 unpackaged 개발 실행과 MSIX 실행의 capability가 다르다.

- [Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr)
- [Windows speech recognition](https://learn.microsoft.com/en-us/windows/apps/develop/input/speech-recognition)

## 결정

### 1. 모델 호출보다 사용자 선택이 먼저다

첫 진입점은 composer의 명시적 `화면 읽기` 동작이다. Desktop이 Windows 시스템 picker를 열고 사용자가 창 또는 디스플레이를 직접 고른 경우에만 한 프레임을 캡처한다. 백그라운드 상시 캡처, 주기 캡처, 모델이 고른 HWND·좌표의 무인 캡처는 제공하지 않는다. 선택 영역은 캡처 preview 안에서 사용자가 직접 자르고 그 영역만 다시 OCR한다.

### 2. 준비와 전송을 분리한다

캡처 결과는 Desktop 메모리의 `PreparedSensitiveContext`로만 보관한다. 준비 항목은 conversation/run/tool-call identity에 묶고 2분 뒤 만료하며 새 캡처, 취소, 거부, 전송 완료, 앱 종료 때 폐기한다. 최대 크기는 한 변 4,096픽셀, 총 16메가픽셀, encoded image 8MiB로 제한한다.

로컬 OCR 결과는 최대 UTF-8 32KiB로 제한한다. preview에서는 이미지와 OCR 텍스트를 함께 보여 주고 사용자가 영역 자르기, 전체 취소, OCR 텍스트 삭제·마스킹을 할 수 있어야 한다. 전송 버튼은 기본 선택하지 않는다. 승인 뒤 첫 수직 기능은 사용자가 확인한 OCR 텍스트만 Agent에 전달하며 이미지/VLM 전송은 별도 모델 capability·크기·비용 preview가 생길 때까지 제공하지 않는다.

### 3. 원문은 영속화하지 않는다

캡처 이미지, OCR 원문, 음성 PCM과 인식 중간 결과는 SQLite, 실행 event, 감사 summary, 진단 ZIP, 로그에 저장하지 않는다. 감사에는 target 종류, 픽셀 크기, OCR 문자 수, 마스킹 여부, 성공·거부·만료 상태만 남긴다. 관리되는 byte buffer는 폐기 시 가능한 범위에서 지우고 모든 bitmap·stream·capture session을 즉시 dispose한다.

### 4. OS capability 실패를 우회하지 않는다

`GraphicsCaptureSession.IsSupported()`가 false이거나 보안 화면·보호 콘텐츠가 캡처를 거부하면 구조화된 `not_supported` 또는 `unavailable`로 끝낸다. Windows OCR package identity가 없으면 `package_identity_required`를 반환하고 온라인 OCR이나 임의 executable로 자동 fallback하지 않는다. Windows 10 22H2와 Windows 11에서 picker, 취소, 보호 콘텐츠, 다중 모니터를 각각 실기 검증한다.

### 5. 음성은 화면과 별도 capability다

STT는 설정에서 명시적으로 켠 뒤 composer의 누르고 말하기 방식으로만 시작한다. 마이크 사용 중임을 항상 표시하고 release·취소·timeout 때 즉시 중단한다. package identity, microphone 권한, Windows privacy 설정을 모두 확인하며 온라인 음성 인식이 필요한 상태를 숨기지 않는다. PCM과 중간 인식 결과는 저장하지 않는다.

TTS는 사용자가 선택한 응답 하나를 읽는 동작으로 시작하고 즉시 중지 control을 제공한다. 자동 읽기와 방해 금지 시간은 별도 opt-in 설정이며 기본은 꺼짐이다. STT와 TTS는 화면 Tool의 승인이나 지속 권한을 공유하지 않는다.

## 구현 순서

1. `PreparedSensitiveContext` 수명·상한·폐기 port와 이미지/OCR preview UI를 구현한다.
2. Windows 시스템 picker 기반 1회 캡처 adapter와 취소·미지원 결과를 연결한다.
3. package identity가 있는 Windows 로컬 OCR adapter를 연결하고 확인된 텍스트만 대화에 합류한다.
4. Windows 10/11 picker·다중 모니터·보호 콘텐츠·메모리 비보존 acceptance를 닫는다.
5. 이후 STT와 TTS를 서로 독립된 opt-in 수직 기능으로 추가한다.

## 결과

- 화면을 읽는 기능은 한 번의 승인 팝업이 아니라 실제 내용 review를 포함한 사용자 주도 흐름이 된다.
- unpackaged 개발 실행에서 OCR이 제한될 수 있지만 이 제약을 숨기거나 cloud 전송으로 우회하지 않는다.
- 이미지 VLM, 상시 화면 감시, 좌표 기반 무인 캡처는 이 결정의 범위 밖이다.
