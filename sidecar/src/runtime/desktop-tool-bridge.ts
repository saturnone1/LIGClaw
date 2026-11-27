import { randomUUID } from "node:crypto";
import type { ToolInvokeParams, ToolResultParams } from "../generated/contracts.js";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "./runtime-policy.js";

interface PendingToolCall {
  readonly resolve: (output: Readonly<Record<string, unknown>>) => void;
  readonly reject: (error: Error) => void;
  readonly timeout: ReturnType<typeof setTimeout>;
  readonly removeAbortListener: () => void;
}

export class DesktopToolBridge {
  private readonly pending = new Map<string, PendingToolCall>();

  constructor(private readonly notify: (method: string, parameters: unknown) => void) {}

  invoke(
    request: Omit<ToolInvokeParams, "toolCallId"> & { readonly toolCallId?: string },
    signal?: AbortSignal,
    timeoutMs = DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
  ): Promise<Readonly<Record<string, unknown>>> {
    const toolCallId = request.toolCallId?.trim() || randomUUID();
    if (this.pending.has(toolCallId)) throw new Error("Duplicate Desktop tool call id.");
    if (signal?.aborted) return Promise.reject(new Error("Desktop tool call was cancelled."));

    return new Promise((resolve, reject) => {
      const abort = () => this.reject(toolCallId, new Error("Desktop tool call was cancelled."));
      signal?.addEventListener("abort", abort, { once: true });
      const timeout = setTimeout(
        () => this.reject(toolCallId, new Error("Desktop tool call timed out.")),
        timeoutMs,
      );
      this.pending.set(toolCallId, {
        resolve,
        reject,
        timeout,
        removeAbortListener: () => signal?.removeEventListener("abort", abort),
      });
      try {
        this.notify("tool.invoke", { ...request, toolCallId } satisfies ToolInvokeParams);
      } catch (error) {
        this.reject(toolCallId, error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  complete(result: ToolResultParams): boolean {
    const pending = this.take(result.toolCallId);
    if (!pending) return false;
    if (result.success) pending.resolve(result.output);
    else pending.reject(new Error(result.error?.trim() || "Desktop tool execution failed."));
    return true;
  }

  dispose(reason = "Desktop tool bridge disconnected."): void {
    for (const toolCallId of [...this.pending.keys()]) this.reject(toolCallId, new Error(reason));
  }

  private reject(toolCallId: string, error: Error): void {
    this.take(toolCallId)?.reject(error);
  }

  private take(toolCallId: string): PendingToolCall | undefined {
    const pending = this.pending.get(toolCallId);
    if (!pending) return undefined;
    this.pending.delete(toolCallId);
    clearTimeout(pending.timeout);
    pending.removeAbortListener();
    return pending;
  }
}
