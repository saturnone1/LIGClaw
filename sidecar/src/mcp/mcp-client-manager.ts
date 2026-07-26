import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { fileURLToPath } from "node:url";
import type { McpCallParams, McpCallResult, McpConfigureParams, McpConfigureResult, McpStatusResult } from "../generated/contracts.js";

const MAXIMUM_CONNECTIONS = 8;
const CONNECT_TIMEOUT_MS = 10_000;
const HEALTH_TIMEOUT_MS = 5_000;
const MAXIMUM_TOOL_PAGES = 5;
const MAXIMUM_DISCOVERED_TOOLS = 128;
const MAXIMUM_ALLOWED_TOOLS = 32;
const MAXIMUM_ARGUMENT_BYTES = 32_768;
const MAXIMUM_RESULT_CHARACTERS = 65_536;
const CALL_TIMEOUT_MS = 30_000;
const CONNECTION_ID_PATTERN = /^[a-z][a-z0-9-]{0,31}$/;

export interface McpConnectionDefinition {
  readonly id: string;
  readonly displayName: string;
  readonly url: URL;
  readonly enabled: boolean;
  readonly allowedTools: readonly string[];
  readonly authorization?: string;
  readonly transport: "streamable_http" | "stdio";
  readonly executableId?: "ligclaw-sample-rag";
}

interface McpRemoteTool {
  readonly name: string;
  readonly description?: string;
  readonly inputSchema?: Readonly<Record<string, unknown>>;
}

export interface McpClientSession {
  listTools(cursor: string | undefined, signal: AbortSignal): Promise<{
    readonly tools: readonly McpRemoteTool[];
    readonly nextCursor?: string;
  }>;
  ping(signal: AbortSignal): Promise<void>;
  callTool(name: string, argumentsValue: Readonly<Record<string, unknown>>, signal: AbortSignal): Promise<unknown>;
  close(): Promise<void>;
}

export type McpSessionConnector = (
  definition: McpConnectionDefinition,
  signal: AbortSignal,
) => Promise<McpClientSession>;

interface ManagedConnection {
  readonly id: string;
  readonly displayName: string;
  state: "connected" | "disabled" | "failed";
  toolCount: number;
  tools: ReadonlyMap<string, McpRemoteTool>;
  allowedTools: ReadonlySet<string>;
  session?: McpClientSession;
  activeCalls: number;
  retiring: boolean;
  retirement?: Promise<void>;
  completeRetirement?: () => void;
}

export class McpClientManager {
  private connections = new Map<string, ManagedConnection>();
  private operation: Promise<void> = Promise.resolve();

  constructor(private readonly connect: McpSessionConnector = connectMcp) {}

  allowedToolCatalog(): readonly { readonly connectionId: string; readonly toolName: string }[] {
    return [...this.connections.values()]
      .filter((connection) => connection.state === "connected")
      .flatMap((connection) => [...connection.allowedTools]
        .filter((toolName) => connection.tools.has(toolName))
        .map((toolName) => ({ connectionId: connection.id, toolName })))
      .sort((left, right) => `${left.connectionId}/${left.toolName}`.localeCompare(`${right.connectionId}/${right.toolName}`));
  }

  configure(parameters: McpConfigureParams): Promise<McpConfigureResult> {
    return this.exclusive(() => this.configureCore(parameters));
  }

  private async configureCore(parameters: McpConfigureParams): Promise<McpConfigureResult> {
    const nextConnections = new Map<string, ManagedConnection>();
    const rawConnections = Array.isArray(parameters?.connections)
      ? parameters.connections.slice(0, MAXIMUM_CONNECTIONS)
      : [];
    const seen = new Set<string>();
    await Promise.all(rawConnections.map(async (raw) => {
      const definition = parseDefinition(raw);
      const fallbackId = typeof raw.id === "string" ? raw.id : "invalid";
      const fallbackName = typeof raw.displayName === "string" && raw.displayName.trim()
        ? raw.displayName.trim().slice(0, 80)
        : fallbackId;
      if (!definition || seen.has(definition.id)) {
        nextConnections.set(fallbackId, failedConnection(fallbackId, fallbackName));
        return;
      }
      seen.add(definition.id);
      if (!definition.enabled) {
        nextConnections.set(definition.id, {
          id: definition.id,
          displayName: definition.displayName,
          state: "disabled",
          toolCount: 0,
          tools: new Map(),
          allowedTools: new Set(definition.allowedTools),
          activeCalls: 0,
          retiring: false,
        });
        return;
      }
      let session: McpClientSession | undefined;
      try {
        const signal = AbortSignal.timeout(CONNECT_TIMEOUT_MS);
        session = await this.connect(definition, signal);
        const tools = await discoverTools(session, signal);
        if (definition.allowedTools.some((name) => !tools.has(name)))
          throw new Error("An allowlisted MCP tool was not discovered.");
        if (nextConnections.has(definition.id)) {
          await closeQuietly(session);
          return;
        }
        nextConnections.set(definition.id, {
          id: definition.id,
          displayName: definition.displayName,
          state: "connected",
          toolCount: tools.size,
          tools,
          allowedTools: new Set(definition.allowedTools),
          session,
          activeCalls: 0,
          retiring: false,
        });
      } catch {
        if (session) await closeQuietly(session);
        nextConnections.set(definition.id, failedConnection(definition.id, definition.displayName));
      }
    }));
    const previousConnections = this.connections;
    this.connections = nextConnections;
    await Promise.all([...previousConnections.values()].map((connection) => retireConnection(connection)));
    return this.snapshot();
  }

  status(): Promise<McpStatusResult> {
    return this.exclusive(() => this.statusCore());
  }

  private async statusCore(): Promise<McpStatusResult> {
    await Promise.all([...this.connections.values()].map(async (connection) => {
      if (connection.state !== "connected" || !connection.session) return;
      try {
        await connection.session.ping(AbortSignal.timeout(HEALTH_TIMEOUT_MS));
      } catch {
        connection.state = "failed";
        connection.toolCount = 0;
        connection.tools = new Map();
        await retireConnection(connection);
      }
    }));
    return this.snapshot();
  }

  call(parameters: McpCallParams): Promise<McpCallResult> {
    // Calls run per session so a slow server cannot serialize healthy MCP connections.
    return this.callCore(parameters);
  }

  private async callCore(parameters: McpCallParams): Promise<McpCallResult> {
    const connection = this.connections.get(parameters.connectionId);
    if (!connection || connection.state !== "connected" || !connection.session)
      throw new Error("MCP connection is unavailable.");
    if (!connection.allowedTools.has(parameters.toolName) || !connection.tools.has(parameters.toolName))
      throw new Error("MCP tool is not allowlisted.");
    if (!isPlainObject(parameters.arguments) || serializedLength(parameters.arguments) > MAXIMUM_ARGUMENT_BYTES)
      throw new Error("MCP tool arguments are invalid or exceed the limit.");
    const session = connection.session;
    connection.activeCalls++;
    try {
      const raw = await session.callTool(
        parameters.toolName,
        parameters.arguments,
        AbortSignal.timeout(CALL_TIMEOUT_MS),
      );
      return normalizeCallResult(raw);
    } finally {
      connection.activeCalls--;
      if (connection.retiring && connection.activeCalls === 0) void finishRetirement(connection);
    }
  }

  close(): Promise<void> {
    return this.exclusive(() => this.closeUnsafe());
  }

  private async closeUnsafe(): Promise<void> {
    const previousConnections = this.connections;
    this.connections = new Map();
    await Promise.all([...previousConnections.values()].map((connection) => retireConnection(connection, true)));
  }

  private async exclusive<T>(operation: () => Promise<T>): Promise<T> {
    const previous = this.operation;
    let release!: () => void;
    this.operation = new Promise<void>((resolve) => { release = resolve; });
    await previous;
    try {
      return await operation();
    } finally {
      release();
    }
  }

  private snapshot(): McpStatusResult {
    return {
      connections: [...this.connections.values()]
        .map(({ id, displayName, state, toolCount, tools, allowedTools }) => ({
          id, displayName, state, toolCount,
          tools: [...tools.keys()].sort((left, right) => left.localeCompare(right)),
          allowedTools: [...allowedTools].sort((left, right) => left.localeCompare(right)),
        }))
        .sort((left, right) => left.id.localeCompare(right.id)),
    };
  }
}

function parseDefinition(raw: Readonly<Record<string, unknown>>): McpConnectionDefinition | undefined {
  if (typeof raw !== "object" || raw === null) return undefined;
  const id = raw.id;
  const displayName = raw.displayName;
  const transport = raw.transport;
  const urlText = raw.url;
  const enabled = raw.enabled;
  const allowedTools = raw.allowedTools;
  const authorization = raw.authorization;
  const executableId = raw.executableId;
  if (typeof id !== "string" || !CONNECTION_ID_PATTERN.test(id) ||
      typeof displayName !== "string" || !displayName.trim() || displayName.trim().length > 80 ||
      (transport !== "streamable_http" && transport !== "stdio") ||
      typeof urlText !== "string" || urlText.length > 2048 ||
      typeof enabled !== "boolean" || raw.readOnly !== true || !Array.isArray(allowedTools) ||
      allowedTools.length > MAXIMUM_ALLOWED_TOOLS ||
      allowedTools.some((name) => typeof name !== "string" || !name.trim() || name.length > 128) ||
      new Set(allowedTools).size !== allowedTools.length) return undefined;
  if (authorization !== undefined &&
      (typeof authorization !== "string" || !authorization.trim() || authorization.length > 1_280 || /[\r\n]/u.test(authorization)))
    return undefined;
  let url: URL;
  try {
    url = new URL(urlText);
  } catch {
    return undefined;
  }
  if (url.username || url.password || url.hash) return undefined;
  if (transport === "streamable_http" && !isAllowedEndpoint(url)) return undefined;
  if (transport === "stdio" &&
      (executableId !== "ligclaw-sample-rag" || url.href !== "stdio://ligclaw-sample-rag" || authorization !== undefined))
    return undefined;
  return {
    id, displayName: displayName.trim(), url, enabled, allowedTools: allowedTools as string[], transport,
    ...(typeof authorization === "string" ? { authorization } : {}),
    ...(executableId === "ligclaw-sample-rag" ? { executableId } : {}),
  };
}

function isAllowedEndpoint(url: URL): boolean {
  if (url.protocol === "https:") return true;
  if (url.protocol !== "http:") return false;
  return url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
}

async function discoverTools(session: McpClientSession, signal: AbortSignal): Promise<ReadonlyMap<string, McpRemoteTool>> {
  const tools = new Map<string, McpRemoteTool>();
  let cursor: string | undefined;
  for (let page = 0; page < MAXIMUM_TOOL_PAGES; page++) {
    const result = await session.listTools(cursor, signal);
    for (const tool of result.tools) {
      if (typeof tool.name !== "string" || !tool.name.trim() || tools.has(tool.name))
        throw new Error("MCP tool catalog is invalid.");
      tools.set(tool.name, tool);
      if (tools.size > MAXIMUM_DISCOVERED_TOOLS) throw new Error("MCP tool catalog exceeds the limit.");
    }
    cursor = result.nextCursor;
    if (!cursor) return tools;
  }
  throw new Error("MCP tool catalog pagination exceeds the limit.");
}

async function connectStreamableHttp(
  definition: McpConnectionDefinition,
  signal: AbortSignal,
): Promise<McpClientSession> {
  const client = new Client({ name: "LIGClaw", version: "0.2.0" }, { capabilities: {} });
  const transport = new StreamableHTTPClientTransport(definition.url, {
    ...(definition.authorization
      ? { requestInit: { headers: { Authorization: `Bearer ${definition.authorization}` } } }
      : {}),
    reconnectionOptions: {
      initialReconnectionDelay: 500,
      maxReconnectionDelay: 5_000,
      reconnectionDelayGrowFactor: 1.5,
      maxRetries: 1,
    },
  });
  try {
    // SDK v1's transport declaration predates exactOptionalPropertyTypes and is runtime-compatible.
    await client.connect(transport as unknown as Transport, { signal, timeout: CONNECT_TIMEOUT_MS });
  } catch (error) {
    await client.close().catch(() => {});
    throw error;
  }
  return {
    async listTools(cursor, requestSignal) {
      const result = await client.listTools(cursor ? { cursor } : undefined, {
        signal: requestSignal,
        timeout: CONNECT_TIMEOUT_MS,
      });
      return {
        tools: result.tools.map((tool) => ({
          name: tool.name,
          ...(tool.description ? { description: tool.description } : {}),
          ...(isPlainObject(tool.inputSchema) ? { inputSchema: tool.inputSchema } : {}),
        })),
        ...(result.nextCursor ? { nextCursor: result.nextCursor } : {}),
      };
    },
    async ping(requestSignal) {
      await client.ping({ signal: requestSignal, timeout: HEALTH_TIMEOUT_MS });
    },
    async callTool(name, argumentsValue, requestSignal) {
      return await client.callTool({ name, arguments: argumentsValue }, undefined, {
        signal: requestSignal,
        timeout: CALL_TIMEOUT_MS,
      });
    },
    async close() {
      await transport.terminateSession().catch(() => {});
      await client.close();
    },
  };
}

async function connectMcp(
  definition: McpConnectionDefinition,
  signal: AbortSignal,
): Promise<McpClientSession> {
  if (definition.transport === "streamable_http") return await connectStreamableHttp(definition, signal);
  if (definition.executableId !== "ligclaw-sample-rag") throw new Error("MCP executable is not registered.");
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("./sample-rag-server.js", import.meta.url))],
    stderr: "pipe",
  });
  return await connectClient(transport, signal);
}

async function connectClient(transport: Transport, signal: AbortSignal): Promise<McpClientSession> {
  const client = new Client({ name: "LIGClaw", version: "0.2.0" }, { capabilities: {} });
  try {
    await client.connect(transport, { signal, timeout: CONNECT_TIMEOUT_MS });
  } catch (error) {
    await client.close().catch(() => {});
    throw error;
  }
  return createClientSession(client, transport);
}

function createClientSession(client: Client, transport: Transport): McpClientSession {
  return {
    async listTools(cursor, requestSignal) {
      const result = await client.listTools(cursor ? { cursor } : undefined, { signal: requestSignal, timeout: CONNECT_TIMEOUT_MS });
      return {
        tools: result.tools.map((tool) => ({
          name: tool.name,
          ...(tool.description ? { description: tool.description } : {}),
          ...(isPlainObject(tool.inputSchema) ? { inputSchema: tool.inputSchema } : {}),
        })),
        ...(result.nextCursor ? { nextCursor: result.nextCursor } : {}),
      };
    },
    async ping(requestSignal) {
      await client.ping({ signal: requestSignal, timeout: HEALTH_TIMEOUT_MS });
    },
    async callTool(name, argumentsValue, requestSignal) {
      return await client.callTool({ name, arguments: argumentsValue }, undefined, { signal: requestSignal, timeout: CALL_TIMEOUT_MS });
    },
    async close() {
      await client.close();
    },
  };
}

function failedConnection(id: string, displayName: string): ManagedConnection {
  return {
    id, displayName, state: "failed", toolCount: 0, tools: new Map(), allowedTools: new Set(),
    activeCalls: 0, retiring: false,
  };
}

async function retireConnection(connection: ManagedConnection, waitForActive = false): Promise<void> {
  if (!connection.retiring) {
    connection.retiring = true;
    if (connection.activeCalls > 0) {
      connection.retirement = new Promise<void>((resolve) => { connection.completeRetirement = resolve; });
    }
  }
  if (connection.activeCalls === 0) await finishRetirement(connection);
  else if (waitForActive && connection.retirement) await connection.retirement;
}

async function finishRetirement(connection: ManagedConnection): Promise<void> {
  const session = connection.session;
  delete connection.session;
  if (session) await closeQuietly(session);
  connection.completeRetirement?.();
  delete connection.completeRetirement;
}

async function closeQuietly(session: McpClientSession): Promise<void> {
  await session.close().catch(() => {});
}

function isPlainObject(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function serializedLength(value: unknown): number {
  try {
    return Buffer.byteLength(JSON.stringify(value), "utf8");
  } catch {
    return Number.POSITIVE_INFINITY;
  }
}

function normalizeCallResult(raw: unknown): McpCallResult {
  if (!isPlainObject(raw)) throw new Error("MCP tool result is invalid.");
  const parts: string[] = [];
  if (Array.isArray(raw.content)) {
    for (const part of raw.content) {
      if (isPlainObject(part) && part.type === "text" && typeof part.text === "string") parts.push(part.text);
    }
  }
  if (isPlainObject(raw.structuredContent)) parts.push(JSON.stringify(raw.structuredContent));
  const combined = parts.join("\n");
  const truncated = combined.length > MAXIMUM_RESULT_CHARACTERS;
  return {
    text: combined.slice(0, MAXIMUM_RESULT_CHARACTERS),
    isError: raw.isError === true,
    truncated,
  };
}
