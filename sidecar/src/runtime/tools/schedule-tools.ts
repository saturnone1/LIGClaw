import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { DesktopToolContext, DesktopToolInvoker, ScheduleCancelInput, ScheduleCreateInput, ScheduleListInput } from "./tool-shared.js";
import { reasonSchema } from "./tool-shared.js";

export function createScheduleTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const create: AgentTool<ScheduleCreateInput, Readonly<Record<string, unknown>>> = {
    name: "schedule_create",
    description: "사용자 승인 후 로컬 Windows 알림을 예약합니다. 몇 분 뒤 같은 상대 요청은 delayMinutes만 사용하고, 특정 현지 시각 요청은 startLocal과 system_get_status가 반환한 timeZoneId를 사용합니다.",
    inputSchema: {
      type: "object", additionalProperties: false,
      required: ["title", "message", "recurrence", "misfirePolicy", "reason"],
      oneOf: [
        { required: ["startLocal", "timeZoneId"], not: { required: ["delayMinutes"] } },
        { required: ["delayMinutes"], not: { anyOf: [{ required: ["startLocal"] }, { required: ["timeZoneId"] }] } },
      ],
      properties: {
        title: { type: "string", minLength: 1, maxLength: 63 },
        message: { type: "string", minLength: 1, maxLength: 255 },
        startLocal: { type: "string", pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$" },
        timeZoneId: { type: "string", minLength: 1, maxLength: 128 },
        delayMinutes: { type: "integer", minimum: 1, maximum: 10_080 },
        recurrence: { type: "string", enum: ["once", "daily", "weekly"] },
        interval: { type: "integer", minimum: 1, maximum: 365 },
        misfirePolicy: { type: "string", enum: ["skip", "run_once_on_resume", "ask"] },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: ScheduleCreateInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("schedule_create"), risk: "R1",
        input: {
          title: input.title, message: input.message,
          ...(Number.isInteger(input.delayMinutes) ? { delayMinutes: input.delayMinutes } : {
            startLocal: input.startLocal, timeZoneId: input.timeZoneId,
          }),
          recurrence: input.recurrence,
          ...(Number.isInteger(input.interval) && input.interval! > 0 ? { interval: input.interval } : {}),
          misfirePolicy: input.misfirePolicy, reason: input.reason,
        },
      }, context.signal);
    },
  };
  const list: AgentTool<ScheduleListInput, Readonly<Record<string, unknown>>> = {
    name: "schedule_list",
    description: "사용자 승인 후 이 PC에 저장된 알림 예약을 조회합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["reason"],
      properties: { includeInactive: { type: "boolean" }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: ScheduleListInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("schedule_list"), risk: "R1",
        input: { ...(input.includeInactive === undefined ? {} : { includeInactive: input.includeInactive }), reason: input.reason },
      }, context.signal);
    },
  };
  const cancel: AgentTool<ScheduleCancelInput, Readonly<Record<string, unknown>>> = {
    name: "schedule_cancel",
    description: "schedule_list가 반환한 ID의 활성 알림 예약 하나를 사용자 승인 후 취소합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["jobId", "reason"],
      properties: { jobId: { type: "string", pattern: "^[0-9a-f]{32}$" }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: ScheduleCancelInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("schedule_cancel"), risk: "R1", input: { jobId: input.jobId, reason: input.reason },
      }, context.signal);
    },
  };
  return [create, list, cancel];
}
