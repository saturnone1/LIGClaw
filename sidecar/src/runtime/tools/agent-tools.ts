import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { AgentJobCancelInput, AgentJobControlInput, AgentJobCreateInput, AgentJobListInput, DesktopToolContext, DesktopToolInvoker, SubagentRunInput } from "./tool-shared.js";
import { reasonSchema } from "./tool-shared.js";

function createAgentJobTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const create: AgentTool<AgentJobCreateInput, Readonly<Record<string, unknown>>> = {
    name: "agent_job_create",
    description: "사용자 승인 후 지정 시각에 모델 요청을 실행하는 durable 백그라운드 Agent 작업을 예약합니다. 작업 실행 중 파일 변경이나 Windows 효과는 실행 시점에 별도 승인을 받습니다.",
    inputSchema: {
      type: "object", additionalProperties: false,
      required: ["title", "prompt", "recurrence", "misfirePolicy", "reason"],
      oneOf: [
        { required: ["startLocal", "timeZoneId"], not: { required: ["delayMinutes"] } },
        { required: ["delayMinutes"], not: { anyOf: [{ required: ["startLocal"] }, { required: ["timeZoneId"] }] } },
      ],
      properties: {
        title: { type: "string", minLength: 1, maxLength: 80 },
        prompt: { type: "string", minLength: 1, maxLength: 8000 },
        startLocal: { type: "string", pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$" },
        timeZoneId: { type: "string", minLength: 1, maxLength: 128 },
        delayMinutes: { type: "integer", minimum: 1, maximum: 10_080 },
        recurrence: { type: "string", enum: ["once", "daily", "weekly"] },
        interval: { type: "integer", minimum: 1, maximum: 365 },
        misfirePolicy: { type: "string", enum: ["skip", "run_once_on_resume", "ask"] },
        modelProfileId: { type: "string", minLength: 1, maxLength: 64 },
        maxRuntimeSeconds: { type: "integer", minimum: 30, maximum: 3600 },
        maxAttempts: { type: "integer", minimum: 1, maximum: 5 },
        resultMaxCharacters: { type: "integer", minimum: 1000, maximum: 100000 },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: AgentJobCreateInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("agent_job_create"), risk: "R1",
        input: {
          title: input.title, prompt: input.prompt,
          ...(Number.isInteger(input.delayMinutes) ? { delayMinutes: input.delayMinutes } : {
            startLocal: input.startLocal, timeZoneId: input.timeZoneId,
          }),
          recurrence: input.recurrence,
          ...(Number.isInteger(input.interval) ? { interval: input.interval } : {}),
          misfirePolicy: input.misfirePolicy,
          ...(input.modelProfileId ? { modelProfileId: input.modelProfileId } : {}),
          ...(Number.isInteger(input.maxRuntimeSeconds) ? { maxRuntimeSeconds: input.maxRuntimeSeconds } : {}),
          ...(Number.isInteger(input.maxAttempts) ? { maxAttempts: input.maxAttempts } : {}),
          ...(Number.isInteger(input.resultMaxCharacters) ? { resultMaxCharacters: input.resultMaxCharacters } : {}),
          reason: input.reason,
        },
      }, context.signal);
    },
  };
  const list: AgentTool<AgentJobListInput, Readonly<Record<string, unknown>>> = {
    name: "agent_job_list",
    description: "사용자 승인 후 이 PC에 저장된 durable 백그라운드 Agent 작업을 조회합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["reason"],
      properties: { includeInactive: { type: "boolean" }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: AgentJobListInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("agent_job_list"), risk: "R1",
        input: { ...(input.includeInactive === undefined ? {} : { includeInactive: input.includeInactive }), reason: input.reason },
      }, context.signal);
    },
  };
  const cancel: AgentTool<AgentJobCancelInput, Readonly<Record<string, unknown>>> = {
    name: "agent_job_cancel",
    description: "agent_job_list가 반환한 ID의 대기 중인 백그라운드 Agent 작업 하나를 사용자 승인 후 취소합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["jobId", "reason"],
      properties: { jobId: { type: "string", pattern: "^[0-9a-f]{32}$" }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: AgentJobCancelInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("agent_job_cancel"), risk: "R1", input: { jobId: input.jobId, reason: input.reason },
      }, context.signal);
    },
  };
  const control: AgentTool<AgentJobControlInput, Readonly<Record<string, unknown>>> = {
    name: "agent_job_control",
    description: "agent_job_list가 반환한 백그라운드 Agent 작업을 사용자 승인 후 일시정지·재개하거나 지금 다시 시도합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["jobId", "action", "reason"],
      properties: {
        jobId: { type: "string", pattern: "^[0-9a-f]{32}$" },
        action: { type: "string", enum: ["pause", "resume", "retry_now"] },
        reason: reasonSchema,
      },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: AgentJobControlInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("agent_job_control"), risk: "R1",
        input: { jobId: input.jobId, action: input.action, reason: input.reason },
      }, context.signal);
    },
  };
  return [create, list, cancel, control];
}

function createSubagentTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const run: AgentTool<SubagentRunInput, Readonly<Record<string, unknown>>> = {
    name: "subagent_run",
    description: "서로 독립적인 로컬 분석 작업 1~4개를 사용자 승인 후 최대 3개씩 병렬 실행하고 결과를 합칩니다. 하위 Agent 권한은 R0 또는 R1 상한을 가지며 추가 하위 Agent를 만들 수 없습니다.",
    inputSchema: {
      type: "object", additionalProperties: false,
      required: ["tasks", "maxRisk", "maxRuntimeSeconds", "resultMaxCharacters", "reason"],
      properties: {
        tasks: {
          type: "array", minItems: 1, maxItems: 4,
          items: {
            type: "object", additionalProperties: false, required: ["title", "prompt"],
            properties: {
              title: { type: "string", minLength: 1, maxLength: 80 },
              prompt: { type: "string", minLength: 1, maxLength: 4000 },
            },
          },
        },
        modelProfileId: { type: "string", pattern: "^[A-Za-z0-9_-]{1,64}$" },
        maxRisk: { type: "string", enum: ["R0", "R1"] },
        maxRuntimeSeconds: { type: "integer", minimum: 30, maximum: 900 },
        resultMaxCharacters: { type: "integer", minimum: 1000, maximum: 20000 },
        reason: reasonSchema,
      },
    },
    timeoutMs: 1_200_000,
    retryable: false,
    async execute(input: SubagentRunInput, context: DesktopToolContext) {
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}), conversationId, runId,
        name: desktopToolName("subagent_run"), risk: "R1",
        input: {
          tasks: input.tasks,
          ...(input.modelProfileId ? { modelProfileId: input.modelProfileId } : {}),
          maxRisk: input.maxRisk,
          maxRuntimeSeconds: input.maxRuntimeSeconds,
          resultMaxCharacters: input.resultMaxCharacters,
          reason: input.reason,
        },
      }, context.signal, 1_200_000);
    },
  };
  return [run];
}

export function createAgentTools(bridge: DesktopToolInvoker, conversationId: string, runId: string): readonly AgentTool[] {
  return [...createAgentJobTools(bridge, conversationId, runId), ...createSubagentTools(bridge, conversationId, runId)];
}
