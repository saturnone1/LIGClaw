import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { DesktopToolContext, DesktopToolInvoker } from "./tool-shared.js";

interface CapabilityBuilderOpenInput {
  readonly intent: "create" | "revise";
}

const INPUT_KEYS = new Set(["intent"]);
const RESULT_KEYS = new Set(["accepted", "intent"]);

export function createBuilderTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const open: AgentTool<CapabilityBuilderOpenInput, Readonly<Record<string, unknown>>> = {
    name: "capability_builder_open",
    description: [
      "사용자의 현재 메시지가 LIGClaw에 새 기능을 추가하거나 기존 기능을 수정하려는 요청일 때 Feature Builder를 엽니다.",
      "새 기능은 create, 기존 기능 수정은 revise만 선택하세요.",
      "요구사항 문구를 생성하거나 요약해 전달하지 마세요. Desktop이 현재 run에 저장한 사용자 원문을 직접 사용합니다.",
    ].join(" "),
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["intent"],
      properties: {
        intent: { type: "string", enum: ["create", "revise"] },
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: CapabilityBuilderOpenInput, context: DesktopToolContext) {
      const intent = validateInput(input);
      const result = await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("capability_builder_open"),
        risk: "R0",
        input: { intent },
      }, context.signal);
      return validateResult(result, intent);
    },
  };
  return [open];
}

function validateInput(value: unknown): "create" | "revise" {
  if (!isRecord(value) || !hasExactKeys(value, INPUT_KEYS) ||
      (value.intent !== "create" && value.intent !== "revise"))
    throw new Error("Feature Builder intent is invalid.");
  return value.intent;
}

function validateResult(
  value: Readonly<Record<string, unknown>>,
  expectedIntent: "create" | "revise",
): Readonly<Record<string, unknown>> {
  if (!isRecord(value) || !hasExactKeys(value, RESULT_KEYS) ||
      value.accepted !== true || value.intent !== expectedIntent)
    throw new Error("Desktop returned an invalid Feature Builder acknowledgement.");
  return Object.freeze({ accepted: true, intent: expectedIntent });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, expected: ReadonlySet<string>): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.size && keys.every(key => expected.has(key));
}
