import type { AgentEvent, ConversationStartParams } from "../generated/contracts.js";

export interface RuntimeRunRequest extends ConversationStartParams {
  readonly runId: string;
}

export type RuntimeEventPayload = Pick<AgentEvent, "type" | "text" | "message">;

export type RuntimeEventSink = (event: RuntimeEventPayload) => void;

export interface AgentRuntimeAdapter {
  readonly kind: ConversationStartParams["runtime"];
  run(request: RuntimeRunRequest, emit: RuntimeEventSink, signal: AbortSignal): Promise<void>;
}
