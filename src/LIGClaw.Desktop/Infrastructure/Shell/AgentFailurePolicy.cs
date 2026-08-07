namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static class AgentFailurePolicy
{
    public static string ToUserMessage(string? failureCode) => failureCode switch
    {
        "runtime.provider_authentication" => "모델 API 키 인증에 실패했습니다. 설정에서 새 키를 확인해 주세요.",
        "runtime.provider_permission" => "현재 API 키로 이 모델을 사용할 권한이 없습니다.",
        "runtime.provider_model_not_found" => "설정한 모델을 공급자에서 찾지 못했습니다.",
        "runtime.provider_rate_limited" => "모델 요청 한도에 도달했습니다. 잠시 후 다시 시도해 주세요.",
        "runtime.provider_request_invalid" => "모델 공급자가 대화 요청 형식을 거부했습니다.",
        "runtime.provider_unavailable" => "모델 공급자 서버가 일시적으로 응답하지 않습니다.",
        "runtime.network_unavailable" => "모델 서버에 네트워크로 연결하지 못했습니다.",
        "runtime.timeout" => "모델 응답 제한 시간을 초과했습니다.",
        "runtime.configuration" => "모델 런타임 설정이 올바르지 않습니다.",
        "runtime.context_limit" => "대화 내용이 모델의 최대 컨텍스트 범위를 초과했습니다.",
        _ => "요청을 처리하지 못했어요. 다시 시도해 주세요.",
    };
}
