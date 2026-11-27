# ADR 0023: 제한된 Windows 관리 상태 관측

## 결정

물리 디스크·BitLocker·Defender는 Desktop의 고정 namespace·query·property allowlist를 사용하는 Windows WMI COM adapter로 읽는다. 방화벽과 Windows Update는 각각 고정 Windows COM provider의 읽기 속성만 사용한다. 입력 없는 R0 Tool `system.get_disk_health.v1`과 `system.get_security_status.v1`로 노출하고, provider별 실패는 `unavailable`로 격리한다.

## 이유

범용 PowerShell이나 shell을 열지 않고 Windows 버전별 선택 기능을 관측해야 한다. late-bound OS provider를 사용하면 새 런타임 의존성과 명령 실행 없이 Windows 10/11 공통 경로를 유지할 수 있다.

## 경계

- namespace, query, COM ProgID와 반환 property는 코드에 고정한다.
- 복구 키, 보안 제품 경로, 업데이트 제목·사용자 데이터는 반환하지 않는다.
- 최대 32개 장치·볼륨만 반환하며 문자열과 숫자를 정규화한다.
- 관리자 권한·provider 부재·장치별 오류는 다른 provider 결과를 폐기하지 않는다.
- 어떤 설정도 변경하지 않으며 변경 기능은 별도 위험 등급과 승인 계약이 필요하다.
