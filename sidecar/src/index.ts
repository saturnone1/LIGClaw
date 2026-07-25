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
  type ProviderConfigureParams,
  type ProviderConfigureResult,
  type ProviderTestResult,
  type RpcRequest,
  type RpcResponse,
  type ToolResultParams,
} from "./protocol.js";
import { ClineAgentRuntimeAdapter, createDeterministicSpikeModel } from "./runtime/cline-agent-runtime-adapter.js";
import { ReplayAgentRuntimeAdapter } from "./runtime/replay-agent-runtime-adapter.js";
import { RuntimeCoordinator } from "./runtime/runtime-coordinator.js";
import { DesktopToolBridge } from "./runtime/desktop-tool-bridge.js";

interface StartupOptions {
  readonly pipeName: string;
  readonly sessionToken: string;
}

interface ConnectionState {
  initialized: boolean;
}

function parseOptions(args: readonly string[]): StartupOptions {
  const pipeIndex = args.indexOf("--pipe");
  const pipeName = pipeIndex >= 0 ? args[pipeIndex + 1] : undefined;
  const sessionToken = process.env.LIGCLAW_SESSION_TOKEN;
  if (!pipeName || !sessionToken || sessionToken.length < 32) {
    throw new Error("Sidecar requires --pipe and the LIGCLAW_SESSION_TOKEN environment variable.");
  }
  delete process.env.LIGCLAW_SESSION_TOKEN;
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
      capabilities: ["health.ping", "conversation.start", "conversation.cancel", "agent.events", "provider.configure", "provider.test", "tool.invoke", "tool.result", "tool.system.get_status.v1", "runtime.cline.0.0.65"],
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
    return success(request.id, await testProvider(parameters));
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
    setImmediate(() => process.exit(0));
    return success(request.id, { accepted: true });
  }
  return failure(request.id, -32601, `Unknown method: ${request.method}`);
}

function main(): void {
  const options = parseOptions(process.argv.slice(2));
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
    setImmediate(() => process.exit(process.exitCode ?? 0));
  });
  socket.on("error", (error) => {
    process.stderr.write(`sidecar pipe error: ${error.message}\n`);
    process.exitCode = 1;
  });
  socket.pipe(decoder);
  decoder.on("data", async (message: unknown) => {
    let response: RpcResponse;
    try {
      response = isRpcRequest(message)
        ? await handleRequest(message, options.sessionToken, state, coordinator, clineRuntime, toolBridge, notify)
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

function isProviderConfiguration(value: ProviderConfigureParams | undefined): value is ProviderConfigureParams {
  if (!value || !value.apiKey?.trim() || !value.model?.trim()) return false;
  try {
    const url = new URL(value.baseUrl);
    return url.protocol === "https:" || url.protocol === "http:";
  } catch {
    return false;
  }
}

async function testProvider(configuration: ProviderConfigureParams): Promise<ProviderTestResult> {
  const runtime = new ClineAgentRuntimeAdapter();
  runtime.configure(configuration);
  let failed = false;
  let receivedText = false;
  try {
    await runtime.run(
      { conversationId: "provider-test", runId: "provider-test", input: "Reply with OK.", runtime: "cline" },
      (event) => {
        if (event.type === "run_failed") failed = true;
        if (event.type === "text_delta" && event.text) receivedText = true;
      },
      AbortSignal.timeout(30_000),
    );
    return failed || !receivedText
      ? { success: false, message: "연결하지 못했습니다. 입력값과 네트워크를 확인해 주세요." }
      : { success: true, message: "모델 연결을 확인했어요." };
  } catch {
    return { success: false, message: "연결하지 못했습니다. 입력값과 네트워크를 확인해 주세요." };
  }
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
