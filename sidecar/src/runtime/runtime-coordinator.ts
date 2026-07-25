import type { AgentEvent, ConversationCancelResult, ConversationStartParams, ConversationStartResult } from "../generated/contracts.js";
import type { AgentRuntimeAdapter, RuntimeEventPayload } from "./contracts.js";

interface ActiveRun {
  readonly runId: string;
  readonly cancellation: AbortController;
}

export class RuntimeCoordinator {
  private readonly adapters: ReadonlyMap<string, AgentRuntimeAdapter>;
  private readonly activeRuns = new Map<string, ActiveRun>();
  private readonly activeRunIds = new Set<string>();

  constructor(adapters: readonly AgentRuntimeAdapter[]) {
    this.adapters = new Map(adapters.map((adapter) => [adapter.kind, adapter]));
  }

  start(parameters: ConversationStartParams, notify: (event: AgentEvent) => void): ConversationStartResult {
    if (!parameters.conversationId.trim() || !parameters.runId.trim() || !parameters.input.trim())
      throw new Error("Conversation, run, and input are required.");
    if (this.activeRuns.has(parameters.conversationId)) throw new Error("A run is already active for this conversation.");
    if (this.activeRunIds.has(parameters.runId)) throw new Error("The run id is already active.");
    const adapter = this.adapters.get(parameters.runtime);
    if (!adapter) throw new Error(`Runtime '${parameters.runtime}' is not available.`);

    const runId = parameters.runId;
    const cancellation = new AbortController();
    this.activeRuns.set(parameters.conversationId, { runId, cancellation });
    this.activeRunIds.add(runId);
    let sequence = 0;
    let terminalEventEmitted = false;
    const emit = (event: RuntimeEventPayload) => {
      const isTerminal = event.type === "run_completed" || event.type === "run_cancelled" || event.type === "run_failed";
      if (terminalEventEmitted) return;
      if (isTerminal) terminalEventEmitted = true;
      notify({
        conversationId: parameters.conversationId,
        runId,
        sequence: sequence++,
        type: event.type,
        timestampUtc: new Date().toISOString(),
        ...(event.text === undefined ? {} : { text: event.text }),
        ...(event.message === undefined ? {} : { message: event.message }),
      });
    };

    void adapter.run(parameters, emit, cancellation.signal)
      .catch((error: unknown) => emit(cancellation.signal.aborted
        ? { type: "run_cancelled" }
        : { type: "run_failed", message: toErrorMessage(error) }))
      .finally(() => {
        const current = this.activeRuns.get(parameters.conversationId);
        if (current?.runId === runId) {
          this.activeRuns.delete(parameters.conversationId);
          this.activeRunIds.delete(runId);
        }
      });
    return { accepted: true, runId };
  }

  cancel(conversationId: string): ConversationCancelResult {
    const active = this.activeRuns.get(conversationId);
    if (!active) return { cancelled: false };
    active.cancellation.abort("Desktop requested cancellation.");
    return { cancelled: true };
  }

  abortAll(): void {
    for (const active of this.activeRuns.values()) active.cancellation.abort("Sidecar is shutting down.");
    this.activeRuns.clear();
    this.activeRunIds.clear();
  }
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
