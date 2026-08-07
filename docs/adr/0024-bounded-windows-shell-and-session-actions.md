# ADR 0024: 제한된 Windows 셸·세션 동작

창 상태는 Desktop이 발급한 현재 HWND identity를 승인 전후 재검증한 뒤 Win32 `ShowWindow`의 최소화·최대화·복원만 호출한다. Windows 설정은 고정 page ID를 `ms-settings:` allowlist에 매핑한다. 세션 동작은 잠금과 절전만 별도 R2 계약으로 제공하며 로그아웃·재시작·종료·임의 URI는 노출하지 않는다.

모든 동작은 Desktop preview·승인·감사 경로를 통과한다. 절전 요청은 진행 중인 대화의 성공을 의미하지 않으며 OS가 요청을 거부하면 구조화 실패로 반환한다.
