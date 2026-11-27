import {
  AgentRuntime,
  type AgentMessage,
  type AgentModel,
  type AgentModelRequest,
  type AgentRuntimeEvent,
} from "@cline/agents";
import { createHash } from "node:crypto";
import type { ProviderConfigureParams } from "../generated/contracts.js";
import type { AgentRuntimeAdapter, RuntimeEventSink, RuntimeRunRequest } from "./contracts.js";
import type { DesktopToolBridge } from "./desktop-tool-bridge.js";
import {
  createDesktopTools,
  type McpToolCatalogEntry,
} from "./tools/built-in-tool-catalog.js";
import type { DesktopToolInvoker } from "./tools/tool-shared.js";
import { classifyRuntimeFailure } from "./runtime-failure.js";
import { testProviderConnection } from "./provider-connection-probe.js";
import { detectTextToolCallFallback } from "./text-tool-call-fallback.js";
import {
  detectMemoryLookupIntent,
  detectMemoryRememberIntent,
  formatMemoryLookupResult,
  formatMemoryRememberResult,
} from "./memory-query-router.js";
import {
  DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
  EMPTY_MODEL_RESPONSE,
  LIGCLAW_SYSTEM_PROMPT,
  RUNTIME_LIMITS,
} from "./runtime-policy.js";
export const OPENAI_COMPATIBLE_PROVIDER_ID = "openai-compatible";

interface ConversationRuntimeSession {
  readonly agent: AgentRuntime;
  readonly runScope: { runId: string };
  active: boolean;
  retireAfterRun: boolean;
  lastUsed: number;
  readonly routingKey: string;
}

interface RoutedProvider extends ProviderConfigureParams { readonly profileId: string; }

export class ClineAgentRuntimeAdapter implements AgentRuntimeAdapter {
  readonly kind = "cline" as const;
  private provider?: ProviderConfigureParams;
  private mcpTools: readonly McpToolCatalogEntry[] = [];
  private readonly sessions = new Map<string, ConversationRuntimeSession>();

  constructor(
    private readonly model?: AgentModel,
    private readonly desktopToolBridge?: DesktopToolBridge,
    private readonly providerProbe: typeof testProviderConnection = testProviderConnection,
  ) {}

  configure(provider: ProviderConfigureParams): void {
    this.retireSessions();
    this.provider = provider;
  }

  configureMcpTools(tools: readonly McpToolCatalogEntry[]): void {
    this.retireSessions();
    this.mcpTools = [...tools];
  }

  private retireSessions(): void {
    for (const [conversationId, session] of this.sessions) {
      if (session.active) session.retireAfterRun = true;
      else this.sessions.delete(conversationId);
    }
  }

  get isConfigured(): boolean {
    return this.model !== undefined || this.provider !== undefined;
  }

  async run(request: RuntimeRunRequest, emit: RuntimeEventSink, signal: AbortSignal): Promise<void> {
    const memoryRemember = detectMemoryRememberIntent(request.input);
    if (memoryRemember && this.desktopToolBridge) {
      emit({ type: "run_started" });
      try {
        const output = await this.desktopToolBridge.invoke({
          conversationId: request.conversationId,
          runId: request.runId,
          name: "memory.remember.v1",
          risk: "R1",
          input: {
            kind: "preference",
            key: memoryRemember.key,
            value: memoryRemember.value,
            sensitivity: "general",
            reason: "사용자가 개인 선호를 기억해 달라고 명시적으로 요청했기 때문에",
          },
        }, signal, DESKTOP_TOOL_RESPONSE_TIMEOUT_MS);
        emit({ type: "text_delta", text: formatMemoryRememberResult(memoryRemember, output) });
        emit({ type: "run_completed" });
      } catch {
        if (signal.aborted) emit({ type: "run_cancelled" });
        else {
          emit({ type: "text_delta", text: "개인 선호 저장이 승인되지 않았거나 완료되지 않았습니다." });
          emit({ type: "run_completed" });
        }
      }
      return;
    }
    const memoryLookup = detectMemoryLookupIntent(request.input);
    if (memoryLookup && this.desktopToolBridge) {
      emit({ type: "run_started" });
      try {
        const output = await this.desktopToolBridge.invoke({
          conversationId: request.conversationId,
          runId: request.runId,
          name: "memory.list.v1",
          risk: "R1",
          input: {
            query: memoryLookup.query,
            reason: "사용자가 저장된 개인 선호를 조회해 달라고 요청했기 때문에",
          },
        }, signal, DESKTOP_TOOL_RESPONSE_TIMEOUT_MS);
        emit({ type: "text_delta", text: formatMemoryLookupResult(memoryLookup.label, output) });
        emit({ type: "run_completed" });
      } catch {
        if (signal.aborted) emit({ type: "run_cancelled" });
        else {
          emit({ type: "text_delta", text: "저장된 기억 조회가 승인되지 않았거나 완료되지 않았습니다." });
          emit({ type: "run_completed" });
        }
      }
      return;
    }
    const history = parseConversationHistory(request.history);
    const selectedRoute = this.model ? undefined : await this.selectProvider(request, emit, signal);
    let session = this.sessions.get(request.conversationId);
    const routingKey = selectedRoute ? providerRoutingKey(selectedRoute) : this.provider ? providerRoutingKey(this.provider) : "injected-model";
    if (session && session.routingKey !== routingKey) {
      this.sessions.delete(request.conversationId);
      session = undefined;
    }
    if (!session) {
      this.evictLeastRecentlyUsedSession();
      session = this.createSession(request, history.messages, selectedRoute);
      this.sessions.set(request.conversationId, session);
    } else if (history.provided) {
      session.agent.restore(history.messages);
    }
    session.runScope.runId = request.runId;
    session.active = true;
    session.lastUsed = Date.now();
    const agent = session.agent;
    let completed = false;
    let aborted = false;
    let failed = false;
    let terminalEventEmitted = false;
    let terminalEvent: ReturnType<typeof mapClineEvent>;
    let textMode: "undecided" | "json-candidate" | "plain" = "undecided";
    let bufferedText = "";
    let sawToolActivity = false;
    let sawUserFacingText = false;
    const unsubscribe = agent.subscribe((event: AgentRuntimeEvent) => {
      if (event.type === "tool-started" || event.type === "tool-finished") sawToolActivity = true;
      const mapped = mapClineEvent(event);
      if (!mapped) return;
      const isTerminal = mapped.type === "run_completed" || mapped.type === "run_cancelled" || mapped.type === "run_failed";
      if (isTerminal) {
        terminalEvent = mapped;
        completed = mapped.type === "run_completed";
        aborted = mapped.type === "run_cancelled";
        failed = mapped.type === "run_failed";
        return;
      }
      if (mapped.type === "text_delta") {
        if (textMode === "plain") {
          sawUserFacingText ||= Boolean(mapped.text?.trim());
          emit(mapped);
          return;
        }
        bufferedText += mapped.text ?? "";
        const trimmed = bufferedText.trimStart();
        if (!trimmed) return;
        if (!sawToolActivity && looksLikeJsonToolPayload(trimmed)) {
          textMode = "json-candidate";
          return;
        }
        textMode = "plain";
        sawUserFacingText ||= Boolean(bufferedText.trim());
        emit({ type: "text_delta", text: bufferedText });
        bufferedText = "";
        return;
      }
      emit(mapped);
    });
    const abort = () => agent.abort(signal.reason ?? "Desktop cancelled the run.");
    signal.addEventListener("abort", abort, { once: true });

    try {
      const result = await agent.run(request.input);
      completed ||= result.status === "completed";
      aborted ||= result.status === "aborted";
      failed ||= result.status === "failed";
      if (looksLikeJsonToolPayload(bufferedText) && !sawToolActivity && result.status === "completed" && this.desktopToolBridge) {
        const fallback = detectTextToolCallFallback(request.input, bufferedText);
        if (fallback) {
          session.retireAfterRun = true;
          try {
            await this.desktopToolBridge.invoke({
              conversationId: request.conversationId,
              runId: request.runId,
              name: fallback.name,
              risk: fallback.risk,
              input: fallback.input,
            }, signal);
            emit({ type: "text_delta", text: fallback.successMessage });
            sawUserFacingText = true;
          } catch {
            if (signal.aborted) {
              emit({ type: "run_cancelled" });
              terminalEventEmitted = true;
            } else {
              emit({ type: "text_delta", text: "승인되지 않았거나 Windows 작업을 완료하지 못했습니다." });
              sawUserFacingText = true;
            }
          }
          if (!terminalEventEmitted) {
            emit({ type: "run_completed" });
            terminalEventEmitted = true;
          }
        }
      }
      if (!terminalEventEmitted && bufferedText.trim()) {
        sawUserFacingText = true;
        emit({ type: "text_delta", text: bufferedText });
      }
      if (!terminalEventEmitted && completed && !sawUserFacingText) {
        emit({ type: "text_delta", text: sawToolActivity ? EMPTY_MODEL_RESPONSE.afterTool : EMPTY_MODEL_RESPONSE.withoutTool });
      }
      if (!terminalEventEmitted) {
        const terminal = terminalEvent ?? {
          type: result.status === "completed" ? "run_completed" : result.status === "aborted" ? "run_cancelled" : "run_failed",
          ...(result.error === undefined ? {} : { message: classifyRuntimeFailure(result.error) }),
        } as const;
        emit(terminal);
        terminalEventEmitted = true;
      }
    } finally {
      signal.removeEventListener("abort", abort);
      unsubscribe();
      session.active = false;
      session.lastUsed = Date.now();
      if ((session.retireAfterRun || !completed || aborted || failed) &&
          this.sessions.get(request.conversationId) === session)
        this.sessions.delete(request.conversationId);
    }
  }

  private createSession(
    request: RuntimeRunRequest,
    initialMessages: readonly AgentMessage[],
    routedProvider?: ProviderConfigureParams,
  ): ConversationRuntimeSession {
    const runScope = { runId: request.runId };
    const scopedBridge: DesktopToolInvoker | undefined = this.desktopToolBridge
      ? {
        invoke: (toolRequest, signal, timeoutMs) => this.desktopToolBridge!.invoke({
          ...toolRequest,
          conversationId: request.conversationId,
          runId: runScope.runId,
        }, signal, timeoutMs),
      }
      : undefined;
    const common = {
      agentId: `ligclaw-${request.conversationId}`,
      conversationId: request.conversationId,
      systemPrompt: LIGCLAW_SYSTEM_PROMPT,
      tools: scopedBridge
        ? createDesktopTools(scopedBridge, request.conversationId, request.runId, this.mcpTools)
        : [],
      initialMessages,
      maxIterations: RUNTIME_LIMITS.maximumAgentIterations,
    } as const;
    const provider = routedProvider ?? this.provider;
    const agent = this.model
      ? new AgentRuntime({ ...common, model: this.model })
      : provider
        ? new AgentRuntime({
          ...common,
          providerId: OPENAI_COMPATIBLE_PROVIDER_ID,
          modelId: provider.model,
          apiKey: provider.apiKey,
          baseUrl: provider.baseUrl,
        })
        : undefined;
    if (!agent) throw new Error("Model connection is not configured.");
    return {
      agent, runScope, active: false, retireAfterRun: false, lastUsed: Date.now(),
      routingKey: provider ? providerRoutingKey(provider) : "injected-model",
    };
  }

  private async selectProvider(
    request: RuntimeRunRequest,
    emit: RuntimeEventSink,
    signal: AbortSignal,
  ): Promise<RoutedProvider | undefined> {
    const selection = await selectProviderRoute(request.providerRouting, this.providerProbe, signal);
    if (selection?.transition) emit({ type: "routing_changed", message: selection.transition });
    return selection?.provider;
  }

  private evictLeastRecentlyUsedSession(): void {
    if (this.sessions.size < RUNTIME_LIMITS.maximumCachedConversations) return;
    let oldest: [string, ConversationRuntimeSession] | undefined;
    for (const entry of this.sessions) {
      if (entry[1].active) continue;
      if (!oldest || entry[1].lastUsed < oldest[1].lastUsed) oldest = entry;
    }
    if (oldest) this.sessions.delete(oldest[0]);
  }
}

function looksLikeJsonToolPayload(text: string): boolean {
  const trimmed = text.trimStart().toLowerCase();
  return trimmed.startsWith("{") || trimmed.startsWith("```json");
}

function parseProviderRouting(value: RuntimeRunRequest["providerRouting"]): {
  readonly primary: RoutedProvider;
  readonly fallbacks: readonly RoutedProvider[];
  readonly allowFallback: boolean;
  readonly explicitSelection: boolean;
} | undefined {
  if (value === undefined || value === null) return undefined;
  const primary = parseRoutedProvider(value.primary);
  const rawFallbacks = value.fallbacks;
  if (!primary || !Array.isArray(rawFallbacks) || rawFallbacks.length > 4 ||
      typeof value.allowFallback !== "boolean" || typeof value.explicitSelection !== "boolean")
    throw new Error("Provider routing snapshot is invalid.");
  const fallbacks = rawFallbacks.map(parseRoutedProvider);
  if (fallbacks.some((candidate) => candidate === undefined))
    throw new Error("Provider routing snapshot is invalid.");
  const resolvedFallbacks = fallbacks as RoutedProvider[];
  const ids = [primary.profileId, ...resolvedFallbacks.map((candidate) => candidate.profileId)];
  if (new Set(ids).size !== ids.length) throw new Error("Provider routing snapshot contains duplicate profiles.");
  return {
    primary,
    fallbacks: resolvedFallbacks,
    allowFallback: value.allowFallback,
    explicitSelection: value.explicitSelection,
  };
}

export async function selectProviderRoute(
  value: RuntimeRunRequest["providerRouting"],
  probe: typeof testProviderConnection,
  signal?: AbortSignal,
): Promise<{ readonly provider: RoutedProvider; readonly transition?: string } | undefined> {
  const routing = parseProviderRouting(value);
  if (!routing) return undefined;
  if (!routing.allowFallback || routing.explicitSelection || routing.fallbacks.length === 0)
    return { provider: routing.primary };

  let reason = "provider_unavailable";
  for (const [index, provider] of [routing.primary, ...routing.fallbacks].entries()) {
    if (signal?.aborted) throw signal.reason ?? new Error("Provider routing cancelled.");
    const result = await cancellableProbe(provider, probe, signal);
    if (result.success)
      return index > 0 ? { provider, transition: `${provider.profileId}|${reason}` } : { provider };
    reason = fallbackReason(result.message);
    if (!isFallbackEligible(reason)) throw new Error(result.message);
  }
  throw new Error("All configured fallback providers are unavailable.");
}

async function cancellableProbe(
  provider: RoutedProvider,
  probe: typeof testProviderConnection,
  signal?: AbortSignal,
) {
  if (!signal) return await probe(provider);
  return await new Promise<Awaited<ReturnType<typeof probe>>>((resolve, reject) => {
    const abort = () => reject(signal.reason ?? new Error("Provider routing cancelled."));
    if (signal.aborted) { abort(); return; }
    signal.addEventListener("abort", abort, { once: true });
    void probe(provider).then(
      (result) => { signal.removeEventListener("abort", abort); resolve(result); },
      (error) => { signal.removeEventListener("abort", abort); reject(error); },
    );
  });
}

function parseRoutedProvider(value: unknown): RoutedProvider | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  const provider = value as Record<string, unknown>;
  if (Object.keys(provider).some((key) => !["profileId", "baseUrl", "apiKey", "model"].includes(key)) ||
      typeof provider.profileId !== "string" || !/^[A-Za-z0-9_-]{1,64}$/.test(provider.profileId) ||
      typeof provider.baseUrl !== "string" || provider.baseUrl.length < 1 || provider.baseUrl.length > 2048 ||
      typeof provider.apiKey !== "string" || provider.apiKey.length < 1 || provider.apiKey.length > 8192 ||
      typeof provider.model !== "string" || provider.model.length < 1 || provider.model.length > 256)
    return undefined;
  try {
    const url = new URL(provider.baseUrl);
    if (url.protocol !== "http:" && url.protocol !== "https:") return undefined;
  } catch { return undefined; }
  return {
    profileId: provider.profileId,
    baseUrl: provider.baseUrl,
    apiKey: provider.apiKey,
    model: provider.model,
  };
}

function providerRoutingKey(provider: ProviderConfigureParams): string {
  const secretHash = createHash("sha256").update(provider.apiKey).digest("hex");
  const profileId = "profileId" in provider ? String(provider.profileId) : "configured";
  return `${profileId}\n${provider.baseUrl}\n${provider.model}\n${secretHash}`;
}

function fallbackReason(message: string): string {
  if (/401|인증/.test(message)) return "provider_authentication";
  if (/404|찾지 못/.test(message)) return "provider_model_not_found";
  if (/429|요청 한도/.test(message)) return "provider_rate_limited";
  if (/408|시간이 초과|네트워크|서버 오류|연결하지 못/.test(message)) return "provider_unavailable";
  if (/500|501|502|503|504|505|506|507|508|509|510|511/.test(message)) return "provider_unavailable";
  return "provider_request_invalid";
}

function isFallbackEligible(reason: string): boolean {
  return reason === "provider_authentication" || reason === "provider_model_not_found" ||
    reason === "provider_rate_limited" || reason === "provider_unavailable";
}

function parseConversationHistory(history: RuntimeRunRequest["history"]): {
  readonly provided: boolean;
  readonly messages: readonly AgentMessage[];
} {
  if (history === undefined || history === null) return { provided: false, messages: [] };
  if (history.length > RUNTIME_LIMITS.maximumHistoryMessages) throw new Error("Conversation history exceeds the message limit.");
  const messages: AgentMessage[] = [];
  let characterCount = 0;
  for (const item of history) {
    const role = item.role;
    const content = item.content;
    if ((role !== "user" && role !== "assistant") || typeof content !== "string" || !content.trim() ||
        content.length > RUNTIME_LIMITS.maximumHistoryMessageCharacters)
      throw new Error("Conversation history is invalid.");
    characterCount += content.length;
    if (characterCount > RUNTIME_LIMITS.maximumHistoryCharacters) throw new Error("Conversation history exceeds the size limit.");
    messages.push({ role, content: [{ type: "text", text: content }] });
  }
  return { provided: true, messages };
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
      ...(event.result.error === undefined ? {} : { message: classifyRuntimeFailure(event.result.error) }),
    };
    case "run-failed": return { type: "run_failed" as const, message: classifyRuntimeFailure(event.error) };
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
      if (userText === "__test_storage_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_storage_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-storage-call",
            toolName: "system_get_storage_status",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "저장소 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_power_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_power_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-power-call",
            toolName: "system_get_power_status",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "전원과 배터리 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_disk_health__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "system_get_disk_health");
        if (!toolResult) {
          yield { type: "tool-call-delta", toolCallId: "deterministic-disk-health-call", toolName: "system_get_disk_health", input: {} };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "물리 디스크와 BitLocker 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_security_status__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "system_get_security_status");
        if (!toolResult) {
          yield { type: "tool-call-delta", toolCallId: "deterministic-security-status-call", toolName: "system_get_security_status", input: {} };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "Windows 보안과 업데이트 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_resource_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_resource_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-resource-call",
            toolName: "system_get_resource_status",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "리소스 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_network_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_network_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-network-call",
            toolName: "system_get_network_status",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "네트워크 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_process_resource_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_process_resource_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-process-resource-call",
            toolName: "system_get_process_resource_status",
            input: { maxResults: 5, reason: "느린 PC 원인을 확인하기 위해" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "CPU와 메모리를 많이 쓰는 앱을 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_network_details__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_network_details"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-network-details-call",
            toolName: "system_get_network_details",
            input: { reason: "네트워크 연결 문제를 확인하기 위해" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "현재 네트워크 주소와 연결 설정을 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_device_status__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_get_device_status"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-device-status-call",
            toolName: "system_get_device_status",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "오디오 출력, 디스플레이와 프린터 상태를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_list_windows__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "app_list_windows"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-list-windows-call",
            toolName: "app_list_windows",
            input: {},
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const windows = typeof toolResult.output === "object" && toolResult.output !== null &&
            "windows" in toolResult.output && Array.isArray(toolResult.output.windows)
          ? toolResult.output.windows
          : [];
        yield { type: "text-delta", text: `열린 앱 창은 ${windows.length}개입니다.` };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_app_search__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "app_search_installed"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-app-search-call",
            toolName: "app_search_installed",
            input: { query: "계산" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const apps = typeof toolResult.output === "object" && toolResult.output !== null &&
            "apps" in toolResult.output && Array.isArray(toolResult.output.apps)
          ? toolResult.output.apps
          : [];
        yield { type: "text-delta", text: `설치 앱 ${apps.length}개를 찾았습니다.` };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_explorer_context__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "explorer_get_context"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-explorer-context-call",
            toolName: "explorer_get_context",
            input: { reason: "사용자가 현재 파일 탐색기 위치와 선택 항목 확인을 요청했기 때문에" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const selected = typeof toolResult.output === "object" && toolResult.output !== null &&
            "selectedItems" in toolResult.output && Array.isArray(toolResult.output.selectedItems)
          ? toolResult.output.selectedItems
          : [];
        yield { type: "text-delta", text: `파일 탐색기 선택 항목은 ${selected.length}개입니다.` };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_show_notification__") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "system_show_notification"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-notification-call",
            toolName: "system_show_notification",
            input: {
              title: "LIGClaw 테스트",
              message: "승인된 알림 경로가 연결되었습니다.",
              reason: "Phase 1 승인 흐름을 검증하기 위해",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const shown = typeof toolResult.output === "object" && toolResult.output !== null &&
          "shown" in toolResult.output && toolResult.output.shown === true;
        yield { type: "text-delta", text: shown ? "승인한 알림을 표시했습니다." : "알림을 표시하지 못했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_launch_app__" || userText === "계산기 열어 줘") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "app_launch"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-app-launch-call",
            toolName: "app_launch",
            input: {
              appName: "Calculator",
              reason: "사용자가 계산기 실행을 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const displayName = typeof toolResult.output === "object" && toolResult.output !== null &&
            "displayName" in toolResult.output
          ? String(toolResult.output.displayName)
          : "앱";
        yield { type: "text-delta", text: `${displayName} 앱을 실행했습니다.` };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_move_file__" || userText === "보고서 파일을 보관 폴더로 옮겨 줘") {
        const toolResult = request.messages
          .flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) =>
            part.type === "tool-result" && part.toolName === "file_move"
          );
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: "deterministic-file-move-call",
            toolName: "file_move",
            input: {
              sources: ["C:\\Phase2Fixture\\report.txt"],
              destinationDirectory: "C:\\Phase2Fixture\\Archive",
              reason: "사용자가 보고서 파일 이동을 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        const succeeded = typeof toolResult.output === "object" && toolResult.output !== null &&
            "succeeded" in toolResult.output
          ? Number(toolResult.output.succeeded)
          : 0;
        yield { type: "text-delta", text: `파일 ${succeeded}개를 이동했습니다.` };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_file_metadata__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "file_get_metadata");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-file-metadata-call", toolName: "file_get_metadata",
            input: { path: "C:\\Work\\report.txt" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "파일 메타데이터를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      const fileMutation = ({
        "__test_file_create_directory__": { toolName: "file_create_directory", input: { path: "C:\\Work\\Reports", reason: "테스트 폴더 생성" } },
        "__test_file_write_text__": { toolName: "file_write_text", input: { path: "C:\\Work\\report.txt", text: "report", reason: "테스트 파일 작성" } },
        "__test_file_zip_create__": { toolName: "file_zip_create", input: { sourcePaths: ["C:\\Work\\a.txt"], destinationPath: "C:\\Work\\bundle.zip", reason: "테스트 압축" } },
        "__test_file_zip_extract__": { toolName: "file_zip_extract", input: { zipPath: "C:\\Work\\bundle.zip", destinationPath: "C:\\Work\\Extracted", reason: "테스트 압축 해제" } },
      } as const)[userText];
      if (fileMutation) {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === fileMutation.toolName);
        if (!toolResult) {
          yield { type: "tool-call-delta", toolCallId: `deterministic-${fileMutation.toolName}-call`, toolName: fileMutation.toolName, input: fileMutation.input };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "파일 작업을 완료했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      const windowsAction = ({
        "__test_app_set_window_state__": { toolName: "app_set_window_state", input: { windowId: "0x1234", state: "maximize", reason: "테스트 창 최대화" } },
        "__test_system_open_settings__": { toolName: "system_open_settings", input: { page: "windows_update", reason: "테스트 설정 열기" } },
        "__test_system_session_action__": { toolName: "system_session_action", input: { action: "lock", reason: "테스트 세션 잠금" } },
        "__test_agent_job_control__": { toolName: "agent_job_control", input: { jobId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", action: "pause", reason: "테스트 작업 일시정지" } },
      } as const)[userText];
      if (windowsAction) {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === windowsAction.toolName);
        if (!toolResult) {
          yield { type: "tool-call-delta", toolCallId: `deterministic-${windowsAction.toolName}-call`, toolName: windowsAction.toolName, input: windowsAction.input };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인된 Windows 작업을 완료했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_file_read_text__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "file_read_text");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-file-read-call", toolName: "file_read_text",
            input: { path: "C:\\Work\\report.txt", reason: "사용자가 보고서의 텍스트 확인을 요청했기 때문에" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인된 파일 텍스트를 읽었습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_mcp_read__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "mcp_read");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-mcp-read-call", toolName: "mcp_read",
            input: {
              connectionId: "knowledge", toolName: "search", arguments: { query: "policy" },
              reason: "사용자가 외부 지식 검색을 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인된 외부 지식을 조회했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_web_fetch__" || userText === "__test_web_search__") {
        const toolName = userText === "__test_web_fetch__" ? "web_fetch" : "web_search";
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === toolName);
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: `${toolName}-call`,
            toolName,
            input: userText === "__test_web_fetch__"
              ? { url: "http://127.0.0.1:8080/document", reason: "사용자가 문서 확인을 요청했기 때문에" }
              : { query: "LIGClaw 정책", reason: "사용자가 관련 자료 검색을 요청했기 때문에" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인된 Web 정보를 조회했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_browser_open__" || userText === "__test_browser_snapshot__") {
        const toolName = userText === "__test_browser_open__" ? "browser_open" : "browser_snapshot";
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === toolName);
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta",
            toolCallId: `${toolName}-call`,
            toolName,
            input: userText === "__test_browser_open__"
              ? { url: "http://127.0.0.1:8080/docs", reason: "사용자가 문서 페이지를 요청했기 때문에" }
              : { browserHandle: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", query: "정책", maxElements: 20, reason: "사용자가 열린 페이지의 정책 내용을 요청했기 때문에" },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "전용 브라우저 정보를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_memory_remember__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "memory_remember");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-memory-remember-call", toolName: "memory_remember",
            input: {
              kind: "alias", key: "주 프로젝트", value: "LIGClaw", sensitivity: "general",
              reason: "사용자가 프로젝트 별칭을 기억해 달라고 명시적으로 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인한 내용을 기억했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_schedule_create__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "schedule_create");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-schedule-create-call", toolName: "schedule_create",
            input: {
              title: "회의 알림", message: "주간 회의가 시작됩니다.", startLocal: "2026-07-27T09:00:00",
              timeZoneId: "Korea Standard Time", recurrence: "weekly", interval: 1,
              misfirePolicy: "run_once_on_resume", reason: "사용자가 매주 회의 알림을 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인한 반복 알림을 예약했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_schedule_relative__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "schedule_create");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-schedule-relative-call", toolName: "schedule_create",
            input: {
              title: "테스트 알림", message: "테스트 시간이 되었습니다.", delayMinutes: 2,
              recurrence: "once", interval: null,
              misfirePolicy: "run_once_on_resume", reason: "사용자가 2분 뒤 알림을 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "2분 뒤 알림을 예약했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_agent_job_create__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "agent_job_create");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-agent-job-create-call", toolName: "agent_job_create",
            input: {
              title: "주간 리소스 보고", prompt: "현재 시스템 리소스를 확인하고 핵심 수치를 요약해 줘.",
              delayMinutes: 5, recurrence: "weekly", interval: 1,
              misfirePolicy: "run_once_on_resume", maxRuntimeSeconds: 180, maxAttempts: 2,
              resultMaxCharacters: 10000, reason: "사용자가 주기적인 시스템 요약을 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "백그라운드 Agent 작업을 예약했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_subagent_run__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "subagent_run");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-subagent-run-call", toolName: "subagent_run",
            input: {
              tasks: [
                { title: "CPU 분석", prompt: "현재 CPU 상태를 분석해 줘." },
                { title: "메모리 분석", prompt: "현재 메모리 상태를 분석해 줘." },
              ],
              maxRisk: "R0", maxRuntimeSeconds: 120, resultMaxCharacters: 5000,
              reason: "독립적인 시스템 분석을 병렬로 수행하기 위해",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "두 하위 Agent의 분석 결과를 합쳤습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_uia_inspect__") {
        const toolResult = request.messages.flatMap((message: AgentMessage) => message.content)
          .find((part: AgentMessage["content"][number]) => part.type === "tool-result" && part.toolName === "uia_inspect");
        if (!toolResult || toolResult.type !== "tool-result") {
          yield {
            type: "tool-call-delta", toolCallId: "deterministic-uia-inspect-call", toolName: "uia_inspect",
            input: {
              windowId: "0x123", query: "저장", maxElements: 20,
              reason: "사용자가 저장 버튼을 찾아 달라고 요청했기 때문에",
            },
          };
          yield { type: "finish", reason: "tool-calls" };
          return;
        }
        yield { type: "text-delta", text: "승인한 창에서 UI 요소를 확인했습니다." };
        yield { type: "finish", reason: "stop" };
        return;
      }
      if (userText === "__test_conversation_context__") {
        const priorUserText = request.messages
          .slice(0, -1)
          .filter((message: AgentMessage) => message.role === "user")
          .flatMap((message: AgentMessage) => message.content)
          .filter((part: AgentMessage["content"][number]): part is Extract<AgentMessage["content"][number], { type: "text" }> => part.type === "text")
          .map((part: Extract<AgentMessage["content"][number], { type: "text" }>) => part.text)
          .at(-1) ?? "없음";
        yield { type: "text-delta", text: `이전 사용자 메시지: ${priorUserText}` };
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
