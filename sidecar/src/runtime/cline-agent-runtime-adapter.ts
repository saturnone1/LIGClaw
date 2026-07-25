import {
  AgentRuntime,
  type AgentMessage,
  type AgentModel,
  type AgentModelRequest,
  type AgentRuntimeEvent,
  type AgentTool,
} from "@cline/agents";
import type { ProviderConfigureParams } from "../generated/contracts.js";
import type { AgentRuntimeAdapter, RuntimeEventSink, RuntimeRunRequest } from "./contracts.js";
import type { DesktopToolBridge } from "./desktop-tool-bridge.js";

const MAXIMUM_AGENT_ITERATIONS = 16;

export class ClineAgentRuntimeAdapter implements AgentRuntimeAdapter {
  readonly kind = "cline" as const;
  private provider?: ProviderConfigureParams;

  constructor(
    private readonly model?: AgentModel,
    private readonly desktopToolBridge?: DesktopToolBridge,
  ) {}

  configure(provider: ProviderConfigureParams): void {
    this.provider = provider;
  }

  get isConfigured(): boolean {
    return this.model !== undefined || this.provider !== undefined;
  }

  async run(request: RuntimeRunRequest, emit: RuntimeEventSink, signal: AbortSignal): Promise<void> {
    const common = {
      agentId: `ligclaw-${request.conversationId}`,
      conversationId: request.conversationId,
      systemPrompt: "당신은 사내 Windows 개인 비서 LIGClaw입니다. 사용자의 언어로 명확하고 간결하게 답하세요. 현재 PC 정보가 필요하면 제공된 Windows Tool을 사용하세요.",
      tools: this.desktopToolBridge ? createDesktopTools(this.desktopToolBridge) : [],
      maxIterations: MAXIMUM_AGENT_ITERATIONS,
    } as const;
    const agent = this.model
      ? new AgentRuntime({ ...common, model: this.model })
      : this.provider
        ? new AgentRuntime({
          ...common,
          providerId: "openai",
          modelId: this.provider.model,
          apiKey: this.provider.apiKey,
          baseUrl: this.provider.baseUrl,
        })
        : undefined;
    if (!agent) throw new Error("Model connection is not configured.");
    let terminalEventEmitted = false;
    const unsubscribe = agent.subscribe((event: AgentRuntimeEvent) => {
      const mapped = mapClineEvent(event);
      if (!mapped) return;
      const isTerminal = mapped.type === "run_completed" || mapped.type === "run_cancelled" || mapped.type === "run_failed";
      if (isTerminal && terminalEventEmitted) return;
      if (isTerminal) {
        terminalEventEmitted = true;
      }
      emit(mapped);
    });
    const abort = () => agent.abort(signal.reason ?? "Desktop cancelled the run.");
    signal.addEventListener("abort", abort, { once: true });

    try {
      const result = await agent.run(request.input);
      if (!terminalEventEmitted) {
        const terminal = {
          type: result.status === "completed" ? "run_completed" : result.status === "aborted" ? "run_cancelled" : "run_failed",
          ...(result.error === undefined ? {} : { message: result.error.message }),
        } as const;
        emit(terminal);
      }
    } finally {
      signal.removeEventListener("abort", abort);
      unsubscribe();
    }
  }
}

function mapClineEvent(event: AgentRuntimeEvent) {
  switch (event.type) {
    case "run-started": return { type: "run_started" as const };
    case "assistant-text-delta": return { type: "text_delta" as const, text: event.text };
    case "run-finished": return {
      type: event.result.status === "completed"
        ? "run_completed" as const
        : event.result.status === "aborted"
          ? "run_cancelled" as const
          : "run_failed" as const,
      ...(event.result.error === undefined ? {} : { message: event.result.error.message }),
    };
    case "run-failed": return { type: "run_failed" as const, message: event.error.message };
    default: return undefined;
  }
}

export function createDeterministicSpikeModel(): AgentModel {
  return {
    async *stream(request: AgentModelRequest) {
      let userText = "";
      for (const message of request.messages) {
        if (message.role !== "user") continue;
        for (const part of message.content) {
          if (part.type === "text") userText = part.text;
        }
      }
      if (userText === "__test_system_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-status-call",
            toolName: "system_get_status",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const release = typeof toolResult.output === "object" && toolResult.output !== null &&
            "windowsRelease" in toolResult.output
          ? String(toolResult.output.windowsRelease)
          : "unknown";
        yield { type: "text-delta", text: `현재 운영체제는 ${release}입니다.` };
        yield { type: "finish", reason: "stop" };
        return;
      }
      for (const text of [
        "요청을 확인했어요.\n\n",
        `“${userText}”\n\n`,
        "현재는 대화 연결을 준비하는 단계예요. Windows 작업 기능이 연결되면 이 요청을 직접 처리할 수 있어요.",
      ]) {
        if (request.signal?.aborted) {
          yield { type: "finish", reason: "aborted" };
          return;
        }
        yield { type: "text-delta", text };
      }
      yield { type: "finish", reason: "stop" };
    },
  };
}

function createDesktopTools(bridge: DesktopToolBridge): readonly AgentTool[] {
  const systemGetStatus: AgentTool<Record<string, never>, Readonly<Record<string, unknown>>> = {
    name: "system_get_status",
    description: "현재 Windows 버전, 시간대, 전원 상태를 확인합니다. 민감한 사용자 데이터는 반환하지 않습니다.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      properties: {},
    },
    timeoutMs: 20_000,
    retryable: false,
    async execute(_input: Record<string, never>, context: DesktopToolContext) {
      if (!context.conversationId || !context.runId) throw new Error("Tool execution context is incomplete.");
      return await bridge.invoke({
        ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
        conversationId: context.conversationId,
        runId: context.runId,
        name: "system.get_status.v1",
        risk: "R0",
        input: {},
      }, context.signal);
    },
  };
  return [systemGetStatus];
}

interface DesktopToolContext {
  readonly conversationId?: string;
  readonly runId?: string;
  readonly toolCallId?: string;
  readonly signal?: AbortSignal;
}
