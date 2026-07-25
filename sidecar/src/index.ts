import net from "node:net";
import process from "node:process";
import { frameMessage, MessageDecoder } from "./framing.js";
import {
  CONTRACT_HASH,
  JSON_RPC_VERSION,
  PROTOCOL_VERSION,
  isRpcRequest,
  type InitializeParams,
  type InitializeResult,
  type RpcRequest,
  type RpcResponse,
} from "./protocol.js";

interface StartupOptions {
  readonly pipeName: string;
  readonly sessionToken: string;
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

function handleRequest(request: RpcRequest, expectedToken: string): RpcResponse {
  if (request.method === "initialize") {
    const parameters = request.params as Partial<InitializeParams> | undefined;
    if (parameters?.sessionToken !== expectedToken) return failure(request.id, -32001, "Invalid session token.");
    if (parameters.protocolVersion !== PROTOCOL_VERSION) return failure(request.id, -32002, "Unsupported protocol version.");
    if (parameters.contractHash !== CONTRACT_HASH) return failure(request.id, -32003, "Contract hash mismatch.");

    const result: InitializeResult = {
      protocolVersion: PROTOCOL_VERSION,
      sidecarVersion: "0.1.0",
      contractHash: CONTRACT_HASH,
      capabilities: ["health.ping"],
    };
    return success(request.id, result);
  }

  if (request.method === "ping") {
    return success(request.id, { timestampUtc: new Date().toISOString() });
  }

  if (request.method === "shutdown") {
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

  socket.on("connect", () => process.stdout.write("sidecar connected\n"));
  socket.on("error", (error) => {
    process.stderr.write(`sidecar pipe error: ${error.message}\n`);
    process.exitCode = 1;
  });
  socket.pipe(decoder);

  decoder.on("data", (message: unknown) => {
    const response = isRpcRequest(message)
      ? handleRequest(message, options.sessionToken)
      : failure("unknown", -32600, "Invalid JSON-RPC request.");
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
