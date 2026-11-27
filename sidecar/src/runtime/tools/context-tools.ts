import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { ClipboardReadInput, ClipboardWriteInput, DesktopToolContext, DesktopToolInvoker, MemoryForgetInput, MemoryListInput, MemoryRememberInput } from "./tool-shared.js";
import { reasonSchema } from "./tool-shared.js";

function createMemoryTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const remember: AgentTool<MemoryRememberInput, Readonly<Record<string, unknown>>> = {
    name: "memory_remember",
    description: "사용자가 명시적으로 기억해 달라고 요청한 별칭, 선호 또는 메모 하나를 승인 후 이 PC에 저장하거나 같은 키로 갱신합니다. API 키, 비밀번호, 인증 토큰은 저장하지 않습니다.",
    inputSchema: {
      type: "object", additionalProperties: false,
      required: ["kind", "key", "value", "sensitivity", "reason"],
      properties: {
        kind: { type: "string", enum: ["alias", "preference", "note"] },
        key: { type: "string", minLength: 1, maxLength: 80 },
        value: { type: "string", minLength: 1, maxLength: 2000 },
        sensitivity: { type: "string", enum: ["general", "personal", "sensitive"] },
        ttlDays: { type: "integer", minimum: 1, maximum: 3650 },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: MemoryRememberInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("memory_remember"), risk: "R1",
        input: {
          kind: input.kind, key: input.key, value: input.value, sensitivity: input.sensitivity,
          ...(Number.isInteger(input.ttlDays) && input.ttlDays! > 0 ? { ttlDays: input.ttlDays } : {}), reason: input.reason,
        },
      }, context.signal);
    },
  };
  const list: AgentTool<MemoryListInput, Readonly<Record<string, unknown>>> = {
    name: "memory_list",
    description: "사용자 승인 후 만료되지 않은 개인 기억을 검색해 최대 50개 모델에 전달합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["reason"],
      properties: { query: { type: "string", minLength: 1, maxLength: 80 }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: MemoryListInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("memory_list"), risk: "R1",
        input: { ...(input.query === undefined ? {} : { query: input.query }), reason: input.reason },
      }, context.signal);
    },
  };
  const forget: AgentTool<MemoryForgetInput, Readonly<Record<string, unknown>>> = {
    name: "memory_forget",
    description: "memory_list가 반환한 ID의 개인 기억 하나를 사용자 승인 후 삭제합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["memoryId", "reason"],
      properties: { memoryId: { type: "string", pattern: "^[0-9a-f]{32}$" }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: MemoryForgetInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("memory_forget"), risk: "R1", input: { memoryId: input.memoryId, reason: input.reason },
      }, context.signal);
    },
  };
  return [remember, list, forget];
}

function createClipboardTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const read: AgentTool<ClipboardReadInput, Readonly<Record<string, unknown>>> = {
    name: "clipboard_read_text",
    description: "사용자 승인 뒤 현재 클립보드 텍스트를 최대 32,768자 읽어 모델 컨텍스트로 반환합니다. 이미지와 파일은 읽지 않습니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["reason"], properties: { reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: ClipboardReadInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("clipboard_read_text"), risk: "R1", input: { reason: input.reason },
      }, context.signal);
    },
  };
  const write: AgentTool<ClipboardWriteInput, Readonly<Record<string, unknown>>> = {
    name: "clipboard_write_text",
    description: "사용자 승인 뒤 최대 32,768자의 텍스트로 현재 클립보드를 교체합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["text", "reason"],
      properties: { text: { type: "string", minLength: 1, maxLength: 32768 }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: ClipboardWriteInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("clipboard_write_text"), risk: "R1", input: { text: input.text, reason: input.reason },
      }, context.signal);
    },
  };
  return [read, write];
}

export function createContextTools(bridge: DesktopToolInvoker, conversationId: string, runId: string): readonly AgentTool[] {
  return [...createClipboardTools(bridge, conversationId, runId), ...createMemoryTools(bridge, conversationId, runId)];
}
