# ADR 0034: bounded Explorer command activation

- 상태: 승인
- 날짜: 2026-08-02

## 결정

Explorer 우클릭 진입점은 Windows 11 상위 메뉴의 공식 `IExplorerCommand`와 MSIX package identity를 사용한다. 임의 레지스트리 static verb를 Windows 11 완성 경로로 간주하지 않는다. native shell DLL은 Explorer 프로세스에서 메뉴 제목·상태와 선택 항목 열거만 빠르게 수행하고, 실제 작업은 `LIGClaw.Desktop.exe --explorer-item <path>` activation으로 Desktop에 넘긴다.

Desktop activation 계약은 기존 파일·폴더의 정규화된 절대 경로만 최대 20개, UTF-8 JSON 32KiB까지 허용한다. 중복은 제거하고 순서를 유지한다. 파일 내용·메타데이터는 읽지 않으며 사용자가 우클릭한 경로 목록을 composer에 preview로 추가할 뿐 자동 전송하거나 모델을 호출하지 않는다. 이미 앱이 실행 중이면 current-user 전용 Named Pipe로 같은 bounded payload를 전달한다.

Windows 10과 Windows 11은 같은 `desktop4:FileExplorerContextMenus` 계약을 사용한다. native DLL은 x64 Explorer와 동일 architecture로 빌드하고 MSIX의 `windows.comServer`와 `windows.fileExplorerContextMenus`에 동일 CLSID로 등록한다. 설치 제거가 등록 수명을 소유한다.

## 현재 구현 상태

argument/parser, 존재 경로 재검증, composer preview, 초기 실행과 이미 실행 중인 앱의 Named Pipe 전달, malformed·초과 payload 거부까지 구현했다. 현재 PC에는 Visual C++ workload와 Windows SDK가 없어 native `IExplorerCommand` DLL·manifest·실제 Explorer 메뉴는 SDK 환경 gate로 남는다.

## 근거

- [Microsoft: packaged desktop app에 File Explorer context menu command 추가](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer)
- [Microsoft: desktop4 FileExplorerContextMenus](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop4-fileexplorercontextmenus)

## 결과

- 선택 항목이 명령줄이나 IPC를 통해 무제한 전달되지 않는다.
- Explorer 프로세스 안에서 모델·SQLite·네트워크·파일 읽기를 수행하지 않는다.
- 개발용 unpackaged 실행에는 공식 상위 메뉴가 없으며, 설치 패키지와 native 빌드 검증 전까지 완료로 표시하지 않는다.
