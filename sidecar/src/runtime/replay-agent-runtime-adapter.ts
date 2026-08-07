import type { AgentRuntimeAdapter, RuntimeEventSink, RuntimeRunRequest } from "./contracts.js";

export class ReplayAgentRuntimeAdapter implements AgentRuntimeAdapter {
  readonly kind = "replay" as const;

  async run(request: RuntimeRunRequest, emit: RuntimeEventSink, signal: AbortSignal): Promise<void> {
    emit({ type: "run_started" });
    const response = `Replay runtime received: ${request.input}`;
    for (const chunk of chunkText(response, 8)) {
      await abortableDelay(20, signal);
      emit({ type: "text_delta", text: chunk });
    }
    emit({ type: "run_completed" });
  }
}

function chunkText(value: string, size: number): readonly string[] {
  const chunks = [];
  for (let offset = 0; offset < value.length; offset += size) chunks.push(value.slice(offset, offset + size));
  return chunks;
}

function abortableDelay(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(signal.reason);
      return;
    }
    const timeout = setTimeout(resolve, milliseconds);
    signal.addEventListener("abort", () => {
      clearTimeout(timeout);
      reject(signal.reason);
    }, { once: true });
  });
}
