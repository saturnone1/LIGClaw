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
  type RpcRequest,
  type RpcResponse,
} from "./protocol.js";
import { ClineAgentRuntimeAdapter, createDeterministicSpikeModel } from "./runtime/cline-agent-runtime-adapter.js";
import { ReplayAgentRuntimeAdapter } from "./runtime/replay-agent-runtime-adapter.js";
import { RuntimeCoordinator } from "./runtime/runtime-coordinator.js";

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

function handleRequest(
  request: RpcRequest,
  expectedToken: string,
  state: ConnectionState,
  coordinator: RuntimeCoordinator,
  notify: (method: string, parameters: unknown) => void,
): RpcResponse {
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
      capabilities: ["health.ping", "conversation.start", "conversation.cancel", "agent.events", "runtime.cline.0.0.65"],
    };
    return success(request.id, result);
  }

  if (!state.initialized) return failure(request.id, -32004, "Sidecar is not initialized.");
  if (request.method === "ping") return success(request.id, { timestampUtc: new Date().toISOString() });
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
  const coordinator = new RuntimeCoordinator([
    new ReplayAgentRuntimeAdapter(),
    new ClineAgentRuntimeAdapter(createDeterministicSpikeModel()),
  ]);
  const notify = (method: string, parameters: unknown) => socket.write(frameMessage({
    jsonrpc: JSON_RPC_VERSION,
    method,
    params: parameters,
  }));

  socket.on("connect", () => process.stdout.write("sidecar connected\n"));
  socket.on("close", () => {
    coordinator.abortAll();
    setImmediate(() => process.exit(process.exitCode ?? 0));
  });
  socket.on("error", (error) => {
    process.stderr.write(`sidecar pipe error: ${error.message}\n`);
    process.exitCode = 1;
  });
  socket.pipe(decoder);
  decoder.on("data", (message: unknown) => {
    let response: RpcResponse;
    try {
      response = isRpcRequest(message)
        ? handleRequest(message, options.sessionToken, state, coordinator, notify)
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

try {
  main();
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
