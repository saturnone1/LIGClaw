import type {
  WorkflowInvokeParams,
  WorkflowInvokeV1Result,
  WorkflowResultParams,
} from "../generated/contracts.js";
import { WORKFLOW_BRIDGE_TIMEOUT_MS } from "./runtime-policy.js";

const WORKFLOW_CALL_ID_PATTERN = /^wfinv_[0-9a-f]{48}$/;
const FIXED_ERROR_CODE_PATTERN = /^[a-z][a-z0-9]*(?:[._][a-z0-9]+)+$/;

interface PendingWorkflowCall {
  readonly resolve: (output: WorkflowInvokeV1Result) => void;
  readonly reject: (error: Error) => void;
  readonly timeout: ReturnType<typeof setTimeout>;
  readonly removeAbortListener: () => void;
}

export interface WorkflowInvoker {
  invoke(
    request: WorkflowInvokeParams,
    signal?: AbortSignal,
    timeoutMs?: number,
  ): Promise<WorkflowInvokeV1Result>;
}

/**
 * Dedicated control-plane bridge for Desktop-owned Workflow execution.
 * This deliberately cannot invoke ordinary Desktop Tools and never carries a
 * Workflow definition, step payload, prepared effect, or execution ticket.
 */
export class DesktopWorkflowBridge implements WorkflowInvoker {
  private readonly pending = new Map<string, PendingWorkflowCall>();
  private disposedReason: string | undefined;

  constructor(private readonly notify: (method: string, parameters: unknown) => void) {}

  invoke(
    request: WorkflowInvokeParams,
    signal?: AbortSignal,
    timeoutMs = WORKFLOW_BRIDGE_TIMEOUT_MS,
  ): Promise<WorkflowInvokeV1Result> {
    const workflowCallId = request.workflowCallId;
    if (this.disposedReason) return Promise.reject(new Error(this.disposedReason));
    if (!WORKFLOW_CALL_ID_PATTERN.test(workflowCallId))
      throw new Error("Workflow call id is invalid.");
    if (this.pending.has(workflowCallId)) throw new Error("Duplicate Workflow call id.");
    if (signal?.aborted) return Promise.reject(new Error("Workflow call was cancelled."));

    return new Promise((resolve, reject) => {
      const abort = () => this.reject(workflowCallId, new Error("Workflow call was cancelled."));
      signal?.addEventListener("abort", abort, { once: true });
      const timeout = setTimeout(
        () => this.reject(workflowCallId, new Error("Workflow call timed out.")),
        timeoutMs,
      );
      this.pending.set(workflowCallId, {
        resolve,
        reject,
        timeout,
        removeAbortListener: () => signal?.removeEventListener("abort", abort),
      });
      try {
        this.notify("workflow.invoke", request);
      } catch (error) {
        this.reject(workflowCallId, error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  complete(result: WorkflowResultParams): boolean {
    const pending = this.take(result.workflowCallId);
    if (!pending) return false;
    if (result.success && result.output && result.error === undefined) {
      pending.resolve(result.output);
    } else if (!result.success && result.output === undefined &&
               typeof result.error === "string" && result.error.length <= 128 &&
               FIXED_ERROR_CODE_PATTERN.test(result.error)) {
      pending.reject(new Error(result.error));
    } else {
      pending.reject(new Error("workflow.bridge_invalid_result"));
    }
    return true;
  }

  dispose(reason = "Workflow bridge disconnected."): void {
    this.disposedReason ??= reason;
    for (const workflowCallId of [...this.pending.keys()])
      this.reject(workflowCallId, new Error(this.disposedReason));
  }

  private reject(workflowCallId: string, error: Error): void {
    this.take(workflowCallId)?.reject(error);
  }

  private take(workflowCallId: string): PendingWorkflowCall | undefined {
    const pending = this.pending.get(workflowCallId);
    if (!pending) return undefined;
    this.pending.delete(workflowCallId);
    clearTimeout(pending.timeout);
    pending.removeAbortListener();
    return pending;
  }
}
