import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { AppLaunchInput, AppSearchInstalledInput, DesktopToolContext, DesktopToolInvoker, ExplorerGetContextInput, WindowActionInput, WindowStateInput } from "./tool-shared.js";
import { reasonSchema, windowActionInputSchema } from "./tool-shared.js";

export function createAppTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const appListWindows: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "app_list_windows",
    description: "현재 보이는 최상위 앱 창의 제목, 프로세스 이름, 활성화·최소화 상태를 조회합니다. 실행 파일 경로나 창 내용은 반환하지 않습니다.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      properties: {},
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(_input: Record<string, never>, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("app_list_windows"),
        risk: "R0",
        input: {},
      }, context.signal);
    },
  };
  const appSearchInstalled: AgentTool<AppSearchInstalledInput, Readonly<Record<string, unknown>>> = {
    name: "app_search_installed",
    description: "시작 메뉴에 등록된 앱을 표시 이름으로 검색합니다. 실행 경로나 명령줄은 반환하지 않으며, 검색 결과의 정확한 표시 이름을 app_launch에 사용할 수 있습니다.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["query"],
      properties: { query: { type: "string", minLength: 1, maxLength: 128 } },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: AppSearchInstalledInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("app_search_installed"),
        risk: "R0",
        input: { query: input.query },
      }, context.signal);
    },
  };
  const explorerGetContext: AgentTool<ExplorerGetContextInput, Readonly<Record<string, unknown>>> = {
    name: "explorer_get_context",
    description: "사용자 승인 뒤 가장 최근 활성 파일 탐색기의 현재 폴더와 선택 항목 경로를 최대 20개 반환합니다. 파일 내용은 읽지 않습니다.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["reason"],
      properties: { reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: ExplorerGetContextInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("explorer_get_context"),
        risk: "R1",
        input: { reason: input.reason },
      }, context.signal);
    },
  };
  const appLaunch: AgentTool<AppLaunchInput, Readonly<Record<string, unknown>>> = {
    name: "app_launch",
    description: "사용자 승인을 받은 뒤 Start Menu에 정확한 이름으로 등록된 앱을 실행합니다. 실행 파일 경로나 명령줄은 사용할 수 없습니다.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["appName", "reason"],
      properties: {
        appName: { type: "string", minLength: 1, maxLength: 128 },
        reason: { type: "string", minLength: 1, maxLength: 160 },
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: AppLaunchInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("app_launch"),
        risk: "R1",
        input: { appName: input.appName, reason: input.reason },
      }, context.signal);
    },
  };
  const appActivate: AgentTool<WindowActionInput, Readonly<Record<string, unknown>>> = {
    name: "app_activate",
    description: "사용자 승인을 받은 뒤 app_list_windows가 반환한 현재 창 ID의 창을 앞으로 가져옵니다.",
    inputSchema: windowActionInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: WindowActionInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("app_activate"),
        risk: "R1",
        input: { windowId: input.windowId, reason: input.reason },
      }, context.signal);
    },
  };
  const appClose: AgentTool<WindowActionInput, Readonly<Record<string, unknown>>> = {
    name: "app_close",
    description: "사용자 명시적 승인을 받은 뒤 app_list_windows가 반환한 현재 창 ID의 창에 정상 닫기를 요청합니다. 저장되지 않은 작업이 있을 수 있습니다.",
    inputSchema: windowActionInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: WindowActionInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("app_close"),
        risk: "R2",
        input: { windowId: input.windowId, reason: input.reason },
      }, context.signal);
    },
  };
  const appSetWindowState: AgentTool<WindowStateInput, Readonly<Record<string, unknown>>> = {
    name: "app_set_window_state",
    description: "사용자 승인 후 app_list_windows가 반환한 현재 창을 최소화·최대화·복원합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["windowId", "state", "reason"],
      properties: {
        windowId: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 18 },
        state: { type: "string", enum: ["minimize", "maximize", "restore"] },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: WindowStateInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("app_set_window_state"), risk: "R1",
        input: { windowId: input.windowId, state: input.state, reason: input.reason },
      }, context.signal);
    },
  };
  return [appListWindows, appSearchInstalled, explorerGetContext, appLaunch, appActivate, appClose, appSetWindowState];
}
