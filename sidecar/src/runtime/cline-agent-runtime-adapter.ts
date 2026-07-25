import { AgentRuntime, type AgentModel, type AgentModelRequest, type AgentRuntimeEvent } from "@cline/agents";
import type { ProviderConfigureParams } from "../generated/contracts.js";
import type { AgentRuntimeAdapter, RuntimeEventSink, RuntimeRunRequest } from "./contracts.js";

const MAXIMUM_AGENT_ITERATIONS = 16;

export class ClineAgentRuntimeAdapter implements AgentRuntimeAdapter {
  readonly kind = "cline" as const;
  private provider?: ProviderConfigureParams;

  constructor(private readonly model?: AgentModel) {}

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
      systemPrompt: "당신은 사내 Windows 개인 비서 LIGClaw입니다. 사용자의 언어로 명확하고 간결하게 답하세요.",
      tools: [],
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
