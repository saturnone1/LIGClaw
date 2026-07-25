import { AgentRuntime, type AgentModel, type AgentModelRequest, type AgentRuntimeEvent } from "@cline/agents";
import type { AgentRuntimeAdapter, RuntimeEventSink, RuntimeRunRequest } from "./contracts.js";

const MAXIMUM_AGENT_ITERATIONS = 16;

export class ClineAgentRuntimeAdapter implements AgentRuntimeAdapter {
  readonly kind = "cline" as const;

  constructor(private readonly model: AgentModel) {}

  async run(request: RuntimeRunRequest, emit: RuntimeEventSink, signal: AbortSignal): Promise<void> {
    const agent = new AgentRuntime({
      agentId: `ligclaw-${request.conversationId}`,
      conversationId: request.conversationId,
      model: this.model,
      systemPrompt: "You are the LIGClaw Windows assistant runtime contract spike.",
      tools: [],
      maxIterations: MAXIMUM_AGENT_ITERATIONS,
    });
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
