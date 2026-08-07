import net from "node:net";
import process from "node:process";
import { frameMessage, MessageDecoder } from "./framing.js";
import {
  CONTRACT_HASH,
  JSON_RPC_VERSION,
  PROTOCOL_VERSION,
  isRpcRequest,
  type ConversationCancelParams,
  type ConversationStartParams,
  type InitializeParams,
  type InitializeResult,
  type McpCallParams,
  type McpConfigureParams,
  type ProviderConfigureParams,
  type ProviderConfigureResult,
  type RpcRequest,
  type RpcResponse,
  type ToolResultParams,
} from "./protocol.js";
import { ClineAgentRuntimeAdapter, createDeterministicSpikeModel } from "./runtime/cline-agent-runtime-adapter.js";
import { ReplayAgentRuntimeAdapter } from "./runtime/replay-agent-runtime-adapter.js";
import { RuntimeCoordinator } from "./runtime/runtime-coordinator.js";
import { DesktopToolBridge } from "./runtime/desktop-tool-bridge.js";
import { testProviderConnection } from "./runtime/provider-connection-probe.js";
import { McpClientManager } from "./mcp/mcp-client-manager.js";
import { BUILT_IN_TOOL_CAPABILITIES } from "./runtime/tools/tool-registration.js";

const RUNTIME_CAPABILITIES = [
  "health.ping",
  "conversation.start",
  "conversation.cancel",
  "agent.events",
  "provider.configure",
  "provider.test",
  "mcp.configure",
  "mcp.status",
  "mcp.call",
  "tool.invoke",
  "tool.result",
] as const;

interface StartupOptions {
  readonly pipeName: string;
  readonly sessionToken: string;
  readonly parentProcessId?: number;
}

interface ConnectionState {
  initialized: boolean;
}

function parseOptions(args: readonly string[]): StartupOptions {
  const pipeIndex = args.indexOf("--pipe");
  const pipeName = pipeIndex >= 0 ? args[pipeIndex + 1] : undefined;
  const sessionToken = process.env.LIGCLAW_SESSION_TOKEN;
  const parentIndex = args.indexOf("--parent-pid");
  const parentProcessId = parentIndex >= 0 ? Number.parseInt(args[parentIndex + 1] ?? "", 10) : undefined;
  if (!pipeName || !sessionToken || sessionToken.length < 32) {
    throw new Error("Sidecar requires --pipe and the LIGCLAW_SESSION_TOKEN environment variable.");
  }
  delete process.env.LIGCLAW_SESSION_TOKEN;
  if (parentProcessId !== undefined && Number.isInteger(parentProcessId) && parentProcessId > 0)
    return { pipeName, sessionToken, parentProcessId };
  return { pipeName, sessionToken };
}

function success(id: string, result: unknown): RpcResponse {
  return { jsonrpc: JSON_RPC_VERSION, id, result };
}

function failure(id: string, code: number, message: string): RpcResponse {
  return { jsonrpc: JSON_RPC_VERSION, id, error: { code, message } };
}

async function handleRequest(
  request: RpcRequest,
  expectedToken: string,
  state: ConnectionState,
  coordinator: RuntimeCoordinator,
  clineRuntime: ClineAgentRuntimeAdapter,
  toolBridge: DesktopToolBridge,
  mcpManager: McpClientManager,
  notify: (method: string, parameters: unknown) => void,
): Promise<RpcResponse> {
  if (request.method === "initialize") {
    const parameters = request.params as Partial<InitializeParams> | undefined;
    if (parameters?.sessionToken !== expectedToken) return failure(request.id, -32001, "Invalid session token.");
    if (parameters.protocolVersion !== PROTOCOL_VERSION) return failure(request.id, -32002, "Unsupported protocol version.");
    if (parameters.contractHash !== CONTRACT_HASH) return failure(request.id, -32003, "Contract hash mismatch.");
    state.initialized = true;
    const result: InitializeResult = {
      protocolVersion: PROTOCOL_VERSION,
      sidecarVersion: "0.2.0",
      contractHash: CONTRACT_HASH,
      capabilities: [...RUNTIME_CAPABILITIES, ...BUILT_IN_TOOL_CAPABILITIES, "runtime.cline.0.0.65"],
    };
    return success(request.id, result);
  }

  if (!state.initialized) return failure(request.id, -32004, "Sidecar is not initialized.");
  if (request.method === "ping") return success(request.id, { timestampUtc: new Date().toISOString() });
  if (request.method === "tool.result") {
    const parameters = request.params as ToolResultParams;
    if (!parameters?.toolCallId?.trim() || typeof parameters.success !== "boolean" ||
        typeof parameters.output !== "object" || parameters.output === null || Array.isArray(parameters.output) ||
        (!parameters.success && !parameters.error?.trim())) {
      return failure(request.id, -32602, "A valid Desktop tool result is required.");
    }
    return success(request.id, { accepted: toolBridge.complete(parameters) });
  }
  if (request.method === "provider.configure" || request.method === "provider.test") {
    const parameters = request.params as ProviderConfigureParams;
    if (!isProviderConfiguration(parameters)) {
      return failure(request.id, -32602, "Base URL, API key, and model are required.");
    }
    if (request.method === "provider.configure") {
      clineRuntime.configure(parameters);
      return success(request.id, { configured: true } satisfies ProviderConfigureResult);
    }
    return success(request.id, await testProviderConnection(parameters));
  }
  if (request.method === "mcp.configure") {
    const parameters = request.params as McpConfigureParams;
    if (!parameters || !Array.isArray(parameters.connections) || parameters.connections.length > 8)
      return failure(request.id, -32602, "A valid bounded MCP connection list is required.");
    const result = await mcpManager.configure(parameters);
    clineRuntime.configureMcpTools(mcpManager.allowedToolCatalog());
    return success(request.id, result);
  }
  if (request.method === "mcp.status") return success(request.id, await mcpManager.status());
  if (request.method === "mcp.call") {
    const parameters = request.params as McpCallParams;
    if (!parameters || typeof parameters.connectionId !== "string" ||
        typeof parameters.toolName !== "string" || typeof parameters.arguments !== "object" ||
        parameters.arguments === null || Array.isArray(parameters.arguments))
      return failure(request.id, -32602, "A valid MCP tool call is required.");
    try {
      return success(request.id, await mcpManager.call(parameters));
    } catch {
      return failure(request.id, -32020, "MCP tool request failed.");
    }
  }
  if (request.method === "conversation.start") {
    try {
      const parameters = request.params as ConversationStartParams;
      const result = coordinator.start(parameters, (event) => notify("agent.event", event));
      return success(request.id, result);
    } catch (error) {
      return failure(request.id, -32602, error instanceof Error ? error.message : String(error));
    }
  }
  if (request.method === "conversation.cancel") {
    const parameters = request.params as Partial<ConversationCancelParams> | undefined;
    if (typeof parameters?.conversationId !== "string" || !parameters.conversationId.trim())
      return failure(request.id, -32602, "conversationId is required.");
    return success(request.id, coordinator.cancel(parameters.conversationId));
  }
  if (request.method === "shutdown") {
    coordinator.abortAll();
    await closeMcpWithinGracePeriod(mcpManager);
    setImmediate(() => process.exit(0));
    return success(request.id, { accepted: true });
  }
  return failure(request.id, -32601, `Unknown method: ${request.method}`);
}

function main(): void {
  const options = parseOptions(process.argv.slice(2));
  if (options.parentProcessId) monitorParentProcess(options.parentProcessId);
  const pipePath = `\\\\.\\pipe\\${options.pipeName}`;
  const socket = net.createConnection(pipePath);
  const decoder = new MessageDecoder();
  const state: ConnectionState = { initialized: false };
  const notify = (method: string, parameters: unknown) => socket.write(frameMessage({
    jsonrpc: JSON_RPC_VERSION,
    method,
    params: parameters,
  }));
  const toolBridge = new DesktopToolBridge(notify);
  const mcpManager = new McpClientManager();
  const useDeterministicModel = process.env.LIGCLAW_TEST_DETERMINISTIC === "1";
  delete process.env.LIGCLAW_TEST_DETERMINISTIC;
  const clineRuntime = new ClineAgentRuntimeAdapter(
    useDeterministicModel ? createDeterministicSpikeModel() : undefined,
    toolBridge,
  );
  const coordinator = new RuntimeCoordinator([
    new ReplayAgentRuntimeAdapter(),
    clineRuntime,
  ]);

  socket.on("connect", () => process.stdout.write("sidecar connected\n"));
  socket.on("close", () => {
    toolBridge.dispose();
    coordinator.abortAll();
    void closeMcpWithinGracePeriod(mcpManager)
      .finally(() => setImmediate(() => process.exit(process.exitCode ?? 0)));
  });
  socket.on("error", (error) => {
    process.stderr.write(`sidecar pipe error: ${error.message}\n`);
    process.exitCode = 1;
    socket.destroy();
  });
  socket.pipe(decoder);
  decoder.on("data", async (message: unknown) => {
    let response: RpcResponse;
    try {
      response = isRpcRequest(message)
        ? await handleRequest(message, options.sessionToken, state, coordinator, clineRuntime, toolBridge, mcpManager, notify)
        : failure("unknown", -32600, "Invalid JSON-RPC request.");
    } catch (error) {
      process.stderr.write(`sidecar request error: ${error instanceof Error ? error.message : String(error)}\n`);
      response = failure(isRpcRequest(message) ? message.id : "unknown", -32603, "Internal sidecar error.");
    }
    socket.write(frameMessage(response));
  });
  decoder.on("error", (error: Error) => {
    process.stderr.write(`sidecar protocol error: ${error.message}\n`);
    socket.destroy();
    process.exitCode = 1;
  });
}

function monitorParentProcess(parentProcessId: number): void {
  const timer = setInterval(() => {
    try {
      process.kill(parentProcessId, 0);
    } catch {
      process.exit(0);
    }
  }, 1_000);
  timer.unref();
}

async function closeMcpWithinGracePeriod(manager: McpClientManager): Promise<void> {
  let timeout: ReturnType<typeof setTimeout> | undefined;
  try {
    await Promise.race([
      manager.close(),
      new Promise<void>((resolve) => { timeout = setTimeout(resolve, 2_000); }),
    ]);
  } finally {
    if (timeout) clearTimeout(timeout);
  }
}

function isProviderConfiguration(value: ProviderConfigureParams | undefined): value is ProviderConfigureParams {
  if (!value || !value.apiKey?.trim() || !value.model?.trim()) return false;
  try {
    const url = new URL(value.baseUrl);
    return url.protocol === "https:" || url.protocol === "http:";
  } catch {
    return false;
  }
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
