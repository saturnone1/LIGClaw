const SECOND_MS = 1_000;
const MINUTE_MS = 60 * SECOND_MS;

// Desktop tool calls include the time a person spends reviewing an approval.
// Keep this policy separate from adapter execution timeouts owned by Desktop.
export const DESKTOP_TOOL_RESPONSE_TIMEOUT_MS = 10 * MINUTE_MS;

export const RUNTIME_LIMITS = Object.freeze({
  maximumAgentIterations: 16,
  maximumCachedConversations: 24,
  maximumHistoryMessages: 40,
  maximumHistoryCharacters: 64_000,
  maximumHistoryMessageCharacters: 20_000,
});

export const EMPTY_MODEL_RESPONSE = Object.freeze({
  withoutTool: "모델이 빈 응답을 반환했습니다. 요청을 다시 보내 주세요.",
  afterTool: "도구 처리는 끝났지만 모델 응답이 비어 있습니다. 실행 활동에서 결과를 확인해 주세요.",
});

export const LIGCLAW_SYSTEM_PROMPT = [
  "당신은 사내 Windows 개인 비서 LIGClaw입니다.",
  "사용자의 언어로 명확하고 간결하게 답하세요.",
  "Windows와 파일 작업은 제공된 Tool만 사용하세요.",
  "Tool 인자 JSON을 일반 답변으로 출력하지 말고 반드시 해당 Tool을 호출하세요.",
  "파일 내용을 읽었다고 추측하지 말고, 영향 있는 작업은 Desktop 승인 결과를 따르세요.",
  "개인 기억은 사용자가 명시적으로 기억·조회·삭제를 요청한 경우에만 memory Tool을 사용하고 API 키, 비밀번호, 인증 토큰은 기억하지 마세요.",
  "사용자가 저장된 별칭이나 선호 키를 Windows 작업 대상으로 직접 언급하면 path, rootPath, destinationDirectory, sources, paths 또는 appName 인자에 $alias:<키> 또는 $preference:<키>를 그대로 전달하세요.",
  "Desktop이 승인 preview 전에 값을 로컬에서 해석하며 값은 모델로 반환되지 않습니다.",
  "‘몇 분/시간 뒤’ 알림은 schedule_create의 delayMinutes를 바로 사용하세요.",
  "특정 현지 시각 예약만 먼저 system_get_status로 시간대를 확인한 뒤 startLocal과 timeZoneId를 전달하세요.",
  "UI Automation은 먼저 app_list_windows로 windowId를 얻고 uia_inspect로 elementId를 발급받은 뒤에만 invoke/set-value/send-text를 사용하세요.",
  "비밀번호 요소, 좌표 클릭, 임의 단축키를 우회해서는 안 됩니다.",
].join(" ");
