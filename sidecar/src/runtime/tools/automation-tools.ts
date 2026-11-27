import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { DesktopToolContext, DesktopToolInvoker, UiaElementInput, UiaInspectInput, UiaSendTextInput, UiaSetValueInput } from "./tool-shared.js";
import { reasonSchema, uiaElementInputSchema } from "./tool-shared.js";

export function createAutomationTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const inspect: AgentTool<UiaInspectInput, Readonly<Record<string, unknown>>> = {
    name: "uia_inspect",
    description: "사용자 승인 후 app_list_windows가 반환한 창 하나의 UIA 요소 이름·종류·지원 동작을 최대 50개 조회합니다. 입력값은 읽지 않으며 반환된 elementId는 잠시만 유효합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["windowId", "reason"],
      properties: {
        windowId: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 18 },
        query: { type: "string", minLength: 1, maxLength: 128 },
        maxElements: { type: "integer", minimum: 1, maximum: 50 },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: UiaInspectInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("uia_inspect"), risk: "R1",
        input: {
          windowId: input.windowId,
          ...(input.query === undefined ? {} : { query: input.query }),
          ...(input.maxElements === undefined ? {} : { maxElements: input.maxElements }),
          reason: input.reason,
        },
      }, context.signal);
    },
  };
  const invoke: AgentTool<UiaElementInput, Readonly<Record<string, unknown>>> = {
    name: "uia_invoke",
    description: "사용자 승인 후 uia_inspect가 발급한 동일 UI 요소를 identity와 대상 창 focus 재확인 뒤 UIA Invoke로 실행합니다.",
    inputSchema: uiaElementInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: UiaElementInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("uia_invoke"), risk: "R2", input: { elementId: input.elementId, reason: input.reason },
      }, context.signal);
    },
  };
  const setValue: AgentTool<UiaSetValueInput, Readonly<Record<string, unknown>>> = {
    name: "uia_set_value",
    description: "사용자 승인 후 비밀번호가 아닌 UIA Value 요소의 값을 identity와 focus 재확인 뒤 설정합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["elementId", "value", "reason"],
      properties: {
        elementId: { type: "string", pattern: "^[0-9a-f]{32}$" },
        value: { type: "string", maxLength: 2000 },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: UiaSetValueInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("uia_set_value"), risk: "R2",
        input: { elementId: input.elementId, value: input.value, reason: input.reason },
      }, context.signal);
    },
  };
  const sendText: AgentTool<UiaSendTextInput, Readonly<Record<string, unknown>>> = {
    name: "uia_send_text",
    description: "사용자 승인 후 비밀번호가 아닌 Edit 요소에 최대 500자의 일반 Unicode 텍스트를 입력합니다. 사용자 입력이나 focus 변경이 감지되면 중단합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["elementId", "text", "reason"],
      properties: {
        elementId: { type: "string", pattern: "^[0-9a-f]{32}$" },
        text: { type: "string", minLength: 1, maxLength: 500 },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: UiaSendTextInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("uia_send_text"), risk: "R2",
        input: { elementId: input.elementId, text: input.text, reason: input.reason },
      }, context.signal);
    },
  };
  return [inspect, invoke, setValue, sendText];
}
