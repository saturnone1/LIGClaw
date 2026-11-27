# LIGClaw E단계 자동 인수 — 기능 카드 플랫폼 출시 후보

> 인수일: 2026-08-12
> 버전: 0.6.0.2
> 자동 인수 상태: 통과
> 배포 서명 상태: LIGClaw Development 자체서명 완료 · Windows 10 설치 ZIP 완료 · 실기 확인 대기

## 범용 기능 카드 플랫폼

- Capabilities 화면은 native WPF 기능 카드 대시보드로 표시된다. 카드에서 이름, 상태, 종류, 정확한 최신/활성 버전, 위험, 출처, 무결성과 갱신 시각을 확인하고 검토·내보내기·rollback으로 이동할 수 있다.
- 빈 대시보드는 `새 기능 만들기`를 다음 행동으로 설명한다. 760×500에서는 한 열, 넓은 화면에서는 여러 카드가 WrapPanel로 배치되며 가로 스크롤을 사용하지 않는다.
- 자연어 요구→추가 질문→정적 검사→무효과 dry-run→검토 초안→사용자 활성화→exact Capability 선택→Workflow 실행→Activity 기록 수직 경로를 `GenericCapabilityBuilderVerticalTests`가 검증한다.
- 메일은 이 범용 구조의 기준 사례로 남으며 제품 목표를 이메일 전용으로 제한하지 않는다.

## Git 기준선

원격 `agent/phase1-desktop-foundation`의 37개 커밋이 강제 재작성됐지만, 로컬 `b98697b`과 원격 `d164d8d`의 파일 트리 차이 및 patch-id 고유 커밋은 0개였다. 로컬 D단계 통합 작업은 pull/rebase로 덮어쓰지 않았다.

## 보안과 의존성

- 공개 advisory 기준 high `fast-uri@3.1.4`와 moderate `hono@4.12.32`를 발견했다.
- 호환 가능한 exact override `fast-uri@3.1.5`, `hono@4.13.1`을 적용했다.
- `npm audit --audit-level=moderate`는 exit 0이며 moderate/high 0건이다.
- 미사용 Dify provider의 `@ai-sdk/provider-utils@3.0.30` low advisory 1건은 호환 가능한 patched 3.x가 없어 계속 추적한다.
- 사내 Nexus가 npm audit API를 제공하지 않는 문제는 package registry와 advisory registry를 분리해 해결했다. 상세 결정은 ADR 0047을 따른다.

## 검증 결과

`./scripts/verify.ps1`:

- PowerShell 5.1 parse와 release manifest/evidence fixture 통과
- contract generation parity, TypeScript check/build 통과
- Sidecar 141/141
- Contracts 24/24
- Application 454/454
- Named Pipe integration 1/1
- Desktop 850/850
- .NET build warning 0, error 0

`./scripts/smoke-ui.ps1 Debug`:

- IntegratedPages 8, TopLevelWindows 1
- CompactLayout 760×500
- keyboard navigation, input focus, field validation, management sorting 통과
- typed SMTP connection, Capability SMTP review, Feature Builder navigation 통과
- ApplicationErrors 0

릴리스 증적:

- MCP cycle 20회 통과
- Desktop/Sidecar 강제 재시작 10회 통과
- 재시작 안정화 후 handle 증가 20개, memory 증가 11,997,184 bytes
- persistence recovery 5/5, diagnostic redaction 3/3 통과
- stage의 `LIGClaw.Desktop.exe`와 bundled `sidecar/node.exe` 실제 동시 기동 통과

첫 release evidence 실행에서 Sidecar 연결 직후 기준값이 Desktop의 비동기 SQLite/WPF/tray 초기화까지 누수로 계산해 handle 증가 582로 실패했다. 30초 idle 대조와 초기화 후 5회 재시작 계측으로 원인을 분리했고, soak 기준선을 10초 안정화 뒤로 이동했다. 한도는 handle 256, memory 128MiB로 유지했다. 수정 후 단독 10회는 handle 23/memory 17,653,760 bytes, 0.6.0.0 pipeline은 handle 20/memory 8,990,720 bytes, 0.6.0.1 pipeline은 handle 26/memory 8,470,528 bytes, 현재 0.6.0.2 pipeline은 handle 20/memory 11,997,184 bytes로 통과했다.

## 설치 산출물

- 파일: `artifacts/msix/0.6.0.2/LIGClaw-0.6.0.2-x64.msix`
- Windows 10 설치 ZIP: `artifacts/LIGClaw-0.6.0.2-Windows10-x64.zip`
- MSIX 크기: 153,839,514 bytes
- MSIX SHA-256: `8d1b75dc7a3dc1da7471da5e455f4add87159f607beaad1a2fae3527797d5011`
- ZIP SHA-256: `9f838cc8de2da79a1797cf266e21b00523613ba07039a91aec8d9d714283d3be`
- Protocol: 1.24
- SQLite schema: 21
- bundled Node: 24.16.0
- staged file count: 18,032
- release manifest/evidence 및 `SHA256SUMS.txt` 생성 완료
- Wpf.Ui 4.3 런타임, `ThirdPartyNotices.txt`, 자체 포함 .NET, bundled Node/Cline Sidecar를 포함한다.
- MSIX는 `CN=LIGClaw Development`로 서명됐고 SignTool `/pa` 검증을 통과했다. 설치 ZIP의 공개 인증서 지문은 `DCEE26DFDEB2FE17EBA13462703E745234302ADF`이며 private key/PFX는 포함하지 않는다.
- 설치 ZIP의 PowerShell은 Windows PowerShell 5.1 parser를 통과하며 관리자로 공개 인증서를 `LocalMachine\\TrustedPeople`에 등록한 뒤 MSIX를 설치한다.
- 서명 빌드는 private key, 유효기간, Code Signing EKU, 신뢰 체인, manifest Publisher 일치를 패키징 전에 검사하며 release candidate 서명에는 HTTPS timestamp를 필수로 요구한다.
- 설치된 앱 실행 확인은 고정 2초 대기 대신 최대 15초 동안 process를 관측해 느린 첫 실행을 오탐하지 않는다.

## 남은 실기·production 배포 후속

- 대상 Windows 10 22H2 PC에서 설치 ZIP 압축 해제→`Install-LIGClaw.cmd` 실행→UAC 승인→시작 메뉴 실행을 확인한다.
- 자체서명 인증서는 사내/개발 배포용이다. 외부 production 배포에는 manifest Publisher와 일치하는 조직 인증서와 timestamp 서명을 사용한다.
- 깨끗한 사용자 계정의 설치→실행→상위 버전 업데이트→제거, 물리 sleep/resume와 대표 실앱 UIA provider matrix는 자동 환경 밖의 수동 인수로 남는다.
