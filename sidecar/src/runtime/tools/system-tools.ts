import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { DesktopToolContext, DesktopToolInvoker, NotificationInput, SystemOpenSettingsInput, SystemSessionActionInput } from "./tool-shared.js";
import { reasonSchema } from "./tool-shared.js";

export function createSystemTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const systemGetStatus: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_status",
    description: "현재 Windows 버전, 시간대, 전원 상태를 확인합니다. 민감한 사용자 데이터는 반환하지 않습니다.",
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
        name: desktopToolName("system_get_status"),
        risk: "R0",
        input: {},
      }, context.signal);
    },
  };
  const systemGetStorageStatus: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_storage_status",
    description: "Windows 논리 볼륨별 루트 경로, 종류, 준비 상태, 파일 시스템, 전체·여유 용량과 사용률을 확인합니다. 파일 내용이나 물리 디스크 SMART 상태는 읽지 않습니다.",
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
        name: desktopToolName("system_get_storage_status"),
        risk: "R0",
        input: {},
      }, context.signal);
    },
  };
  const systemGetDiskHealth: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_disk_health",
    description: "물리 디스크의 종류·크기·Windows Storage health와 BitLocker 보호·암호화 상태를 확인합니다. 복구 키나 파일 내용은 읽지 않습니다.",
    inputSchema: { type: "object", additionalProperties: false, properties: {} },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(_input: Record<string, never>, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId, runId, name: desktopToolName("system_get_disk_health"), risk: "R0", input: {},
      }, context.signal);
    },
  };
  const systemGetSecurityStatus: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_security_status",
    description: "Windows Defender 실시간 보호·서명 나이, 방화벽 프로필과 Windows Update의 최근 성공 상태를 읽기 전용으로 확인합니다.",
    inputSchema: { type: "object", additionalProperties: false, properties: {} },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(_input: Record<string, never>, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId, runId, name: desktopToolName("system_get_security_status"), risk: "R0", input: {},
      }, context.signal);
    },
  };
  const systemGetResourceStatus: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_resource_status",
    description: "현재 Windows의 CPU 사용률, 논리 프로세서 수, 전체·사용 가능 메모리, 메모리 사용률과 부팅 후 경과 시간을 확인합니다.",
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
        name: desktopToolName("system_get_resource_status"),
        risk: "R0",
        input: {},
      }, context.signal);
    },
  };
  const systemGetNetworkStatus: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_network_status",
    description: "현재 Windows 네트워크 사용 가능 여부와 어댑터 이름·종류·연결 상태·링크 속도를 확인합니다. IP, MAC, DNS, 트래픽 내용은 반환하지 않습니다.",
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
        name: desktopToolName("system_get_network_status"),
        risk: "R0",
        input: {},
      }, context.signal);
    },
  };
  const systemShowNotification: AgentTool<NotificationInput, Readonly<Record<string, unknown>>> = {
    name: "system_show_notification",
    description: "사용자 승인을 받은 뒤 이 PC에 Windows 알림 하나를 표시합니다. 알림 제목, 내용, 표시 이유를 모두 제공해야 합니다.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["title", "message", "reason"],
      properties: {
        title: { type: "string", minLength: 1, maxLength: 63 },
        message: { type: "string", minLength: 1, maxLength: 255 },
        reason: { type: "string", minLength: 1, maxLength: 160 },
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
    retryable: false,
    async execute(input: NotificationInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId,
        runId,
        name: desktopToolName("system_show_notification"),
        risk: "R1",
        input: {
          title: input.title,
          message: input.message,
          reason: input.reason,
        },
      }, context.signal);
    },
  };
  const systemOpenSettings: AgentTool<SystemOpenSettingsInput, Readonly<Record<string, unknown>>> = {
    name: "system_open_settings",
    description: "사용자 승인 후 allowlist에 있는 Windows 설정 페이지를 엽니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["page", "reason"],
      properties: {
        page: { type: "string", enum: ["display", "sound", "notifications", "bluetooth", "network", "apps", "storage", "windows_update", "privacy"] },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: SystemOpenSettingsInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("system_open_settings"), risk: "R1", input: { page: input.page, reason: input.reason },
      }, context.signal);
    },
  };
  const systemSessionAction: AgentTool<SystemSessionActionInput, Readonly<Record<string, unknown>>> = {
    name: "system_session_action",
    description: "사용자 명시적 승인 후 Windows 세션을 잠그거나 PC를 절전 모드로 전환합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["action", "reason"],
      properties: { action: { type: "string", enum: ["lock", "sleep"] }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: SystemSessionActionInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("system_session_action"), risk: "R2", input: { action: input.action, reason: input.reason },
      }, context.signal);
    },
  };
  return [systemGetStatus, systemGetStorageStatus, systemGetDiskHealth, systemGetSecurityStatus, systemGetResourceStatus, systemGetNetworkStatus, systemShowNotification, systemOpenSettings, systemSessionAction];
}
