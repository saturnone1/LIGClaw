using LIGClaw.Domain;

namespace LIGClaw.Desktop;

internal static class UserFacingText
{
    internal static string CapabilityType(CapabilityKind kind) => kind switch
    {
        LIGClaw.Domain.CapabilityKind.Prompt => "대화 지침",
        LIGClaw.Domain.CapabilityKind.Workflow => "작업 흐름",
        LIGClaw.Domain.CapabilityKind.Connector => "외부 연결",
        _ => "기능",
    };

    internal static string SourceKind(string sourceKind) => sourceKind switch
    {
        "builder" => "기능 만들기",
        "local_import" => "가져온 기능 파일",
        _ => "이 PC의 기능",
    };

    internal static string ConnectionKind(string kind) => kind switch
    {
        "http" => "웹 서버 연결",
        "mail-api" => "사내 메일 중계 서버",
        "smtp" => "표준 메일 서버",
        _ => "외부 서비스 연결",
    };

    internal static string Risk(string risk) => risk switch
    {
        "R0" => "조회만 함",
        "R1" => "낮은 위험",
        "R2" => "파일·설정 변경",
        "R3" => "외부 전송·높은 영향",
        "R4" => "매우 높은 위험",
        _ => "위험 수준 확인 필요",
    };

    internal static string Tool(string toolName) => toolName switch
    {
        string value when value.StartsWith("Workflow · ", StringComparison.Ordinal) =>
            $"작업 흐름 · {value["Workflow · ".Length..]}",
        "system.get_status.v1" => "컴퓨터 상태 확인",
        "system.get_power_status.v1" => "전원·배터리 상태 확인",
        "system.get_storage_status.v1" => "저장 공간 확인",
        "system.get_disk_health.v1" => "디스크 상태 확인",
        "system.get_security_status.v1" => "보안 상태 확인",
        "system.get_resource_status.v1" => "컴퓨터 자원 상태 확인",
        "system.get_process_resource_status.v1" => "실행 중인 앱 자원 확인",
        "system.get_network_status.v1" => "네트워크 상태 확인",
        "system.get_network_details.v1" => "네트워크 상세 정보 확인",
        "system.get_device_status.v1" => "장치 상태 확인",
        "system.show_notification.v1" => "알림 표시",
        "system.open_settings.v1" => "윈도우 설정 열기",
        "system.session_action.v1" => "윈도우 세션 작업",
        "app.list_windows.v1" => "열린 창 목록 확인",
        "app.search_installed.v1" => "설치된 앱 찾기",
        "app.launch.v1" => "앱 실행",
        "app.activate.v1" => "창 앞으로 가져오기",
        "app.close.v1" => "창 닫기",
        "app.set_window_state.v1" => "창 상태 변경",
        "file.search.v1" => "파일 찾기",
        "file.get_metadata.v1" => "파일 정보 확인",
        "file.read_text.v1" => "문서 내용 읽기",
        "file.open.v1" => "파일 열기",
        "file.copy.v1" => "파일 복사",
        "file.move.v1" => "파일 이동",
        "file.rename.v1" => "파일 이름 변경",
        "file.recycle.v1" => "휴지통으로 이동",
        "file.undo.v1" => "파일 작업 되돌리기",
        "file.create_directory.v1" => "폴더 만들기",
        "file.write_text.v1" => "문서 저장",
        "file.zip_create.v1" => "압축 파일 만들기",
        "file.zip_extract.v1" => "압축 풀기",
        "clipboard.read_text.v1" => "클립보드 글 읽기",
        "clipboard.write_text.v1" => "클립보드에 글 복사",
        "memory.remember.v1" => "기억 저장",
        "memory.list.v1" => "기억 목록 확인",
        "memory.forget.v1" => "기억 삭제",
        "schedule.create.v1" => "알림 예약",
        "schedule.list.v1" => "예약 목록 확인",
        "schedule.cancel.v1" => "예약 취소",
        "agent_job.create.v1" => "자동 작업 만들기",
        "agent_job.list.v1" => "자동 작업 목록 확인",
        "agent_job.cancel.v1" => "자동 작업 취소",
        "agent_job.control.v1" => "자동 작업 상태 변경",
        "subagent.run.v1" => "하위 도우미 실행",
        "web.fetch.v1" => "웹 문서 가져오기",
        "web.search.v1" => "웹 검색",
        "browser.open.v1" => "전용 브라우저 열기",
        "browser.snapshot.v1" => "웹 화면 내용 확인",
        "explorer.get_context.v1" => "파일 탐색기 선택 항목 확인",
        "uia.inspect.v1" => "화면 요소 확인",
        "uia.invoke.v1" => "화면 버튼 누르기",
        "uia.set_value.v1" => "화면 입력값 설정",
        "uia.send_text.v1" => "화면에 글 입력",
        "mcp.read.v1" => "외부 지식 도구 읽기",
        "connection.http.request.v1" => "연결된 웹 서버 조회",
        "mail.create_draft.v1" => "메일 초안 만들기",
        "mail.send.v1" => "메일 보내기",
        "capability.builder.open.v1" => "기능 만들기 열기",
        _ => "등록된 작업 도구",
    };
}
