import assert from "node:assert/strict";
import { test } from "node:test";
import { McpClientManager } from "../dist/mcp/mcp-client-manager.js";

test("MCP configuration isolates failed and disabled connections from a healthy connection", async () => {
  const connected = [];
  const closed = [];
  const manager = new McpClientManager(async (definition) => {
    connected.push(definition.id);
    if (definition.id === "offline") throw new Error("private remote failure detail");
    return session({
      pages: [
        { tools: [{ name: "search" }, { name: "get-document" }], nextCursor: "page-2" },
        { tools: [{ name: "list-sources" }] },
      ],
      onClose: () => closed.push(definition.id),
    });
  });

  const result = await manager.configure({
    connections: [
      connection("knowledge", "Knowledge", "https://rag.example.com/mcp"),
      connection("offline", "Offline", "https://offline.example.com/mcp"),
      connection("paused", "Paused", "https://paused.example.com/mcp", false),
      connection("insecure", "Insecure", "http://example.com/mcp"),
    ],
  });

  assert.deepEqual(result.connections, [
    status("insecure", "Insecure", "failed"),
    status("knowledge", "Knowledge", "connected", ["get-document", "list-sources", "search"]),
    status("offline", "Offline", "failed"),
    status("paused", "Paused", "disabled"),
  ]);
  assert.deepEqual(connected.sort(), ["knowledge", "offline"]);
  await manager.close();
  assert.deepEqual(closed, ["knowledge"]);
});

test("MCP health failure degrades only the affected connection", async () => {
  const manager = new McpClientManager(async (definition) => session({
    pages: [{ tools: [{ name: `${definition.id}-search` }] }],
    failPing: definition.id === "broken",
  }));
  await manager.configure({ connections: [
    connection("broken", "Broken", "https://broken.example.com/mcp"),
    connection("healthy", "Healthy", "https://healthy.example.com/mcp"),
  ] });

  const snapshot = await manager.status();

  assert.deepEqual(snapshot.connections, [
    status("broken", "Broken", "failed"),
    status("healthy", "Healthy", "connected", ["healthy-search"]),
  ]);
  await manager.close();
});

test("MCP configuration rejects duplicate ids and duplicate tool names", async () => {
  const manager = new McpClientManager(async (definition) => session({
    pages: definition.id === "duplicates"
      ? [{ tools: [{ name: "same" }, { name: "same" }] }]
      : [{ tools: [{ name: "search" }] }],
  }));

  const result = await manager.configure({ connections: [
    connection("same-id", "First", "https://first.example.com/mcp"),
    connection("same-id", "Second", "https://second.example.com/mcp"),
    connection("duplicates", "Duplicate tools", "https://tools.example.com/mcp"),
  ] });

  assert.deepEqual(result.connections, [
    status("duplicates", "Duplicate tools", "failed"),
    status("same-id", "Second", "failed"),
  ]);
  await manager.close();
});

test("MCP calls only explicitly allowlisted tools and bounds text results", async () => {
  const manager = new McpClientManager(async () => session({
    pages: [{ tools: [{ name: "search" }, { name: "delete-all" }] }],
  }));
  const definition = connection("knowledge", "Knowledge", "https://rag.example.com/mcp");
  definition.allowedTools = ["search"];
  const configured = await manager.configure({ connections: [definition] });

  assert.deepEqual(configured.connections[0].allowedTools, ["search"]);
  assert.deepEqual(await manager.call({
    connectionId: "knowledge", toolName: "search", arguments: { query: "policy" },
  }), { text: "search:policy", isError: false, truncated: false });
  await assert.rejects(
    manager.call({ connectionId: "knowledge", toolName: "delete-all", arguments: {} }),
    /allowlisted/,
  );
  await manager.close();
});

test("MCP authorization remains connection-scoped and is never returned in status", async () => {
  let received;
  const manager = new McpClientManager(async (definition) => {
    received = definition.authorization;
    return session({ pages: [{ tools: [{ name: "search" }] }] });
  });
  const definition = connection("knowledge", "Knowledge", "https://rag.example.com/mcp");
  definition.authorization = "private-token";

  const result = await manager.configure({ connections: [definition] });

  assert.equal(received, "private-token");
  assert.doesNotMatch(JSON.stringify(result), /private-token/);
  await manager.close();
});

test("registered stdio sample RAG is discovered and called without arbitrary command input", async () => {
  const manager = new McpClientManager();
  const result = await manager.configure({ connections: [{
    id: "sample-rag",
    displayName: "Sample RAG",
    transport: "stdio",
    url: "stdio://ligclaw-sample-rag",
    executableId: "ligclaw-sample-rag",
    enabled: true,
    readOnly: true,
    allowedTools: ["search_knowledge"],
  }] });

  assert.equal(result.connections[0].state, "connected");
  assert.deepEqual(result.connections[0].tools, ["get_document", "search_knowledge"]);
  const call = await manager.call({
    connectionId: "sample-rag",
    toolName: "search_knowledge",
    arguments: { query: "Desktop" },
  });
  assert.match(call.text, /architecture/);
  assert.equal(call.isError, false);
  await manager.close();
});

test("MCP rejects credential injection and oversized call arguments", async () => {
  const manager = new McpClientManager(async () => session({ pages: [{ tools: [{ name: "search" }] }] }));
  const injected = connection("knowledge", "Knowledge", "https://rag.example.com/mcp");
  injected.authorization = "secret\r\nX-Injected: yes";
  const rejected = await manager.configure({ connections: [injected] });
  assert.equal(rejected.connections[0].state, "failed");

  const allowed = connection("knowledge", "Knowledge", "https://rag.example.com/mcp");
  allowed.allowedTools = ["search"];
  await manager.configure({ connections: [allowed] });
  await assert.rejects(manager.call({
    connectionId: "knowledge", toolName: "search", arguments: { query: "x".repeat(40_000) },
  }), /exceed/);
  await manager.close();
});

test("a slow MCP call does not block a healthy connection call", async () => {
  const manager = new McpClientManager(async (definition) => ({
    ...session({ pages: [{ tools: [{ name: "search" }] }] }),
    async callTool() {
      if (definition.id === "slow") await new Promise((resolve) => setTimeout(resolve, 100));
      return { content: [{ type: "text", text: definition.id }] };
    },
  }));
  const slow = connection("slow", "Slow", "https://slow.example/mcp");
  const healthy = connection("healthy", "Healthy", "https://healthy.example/mcp");
  slow.allowedTools = healthy.allowedTools = ["search"];
  await manager.configure({ connections: [slow, healthy] });

  const slowCall = manager.call({ connectionId: "slow", toolName: "search", arguments: {} });
  const healthyCall = await manager.call({ connectionId: "healthy", toolName: "search", arguments: {} });

  assert.equal(healthyCall.text, "healthy");
  assert.equal((await slowCall).text, "slow");
  await manager.close();
});

test("MCP reconfiguration retires a session only after its active call completes", async () => {
  let releaseCall;
  let oldSessionClosed = false;
  const callBlocked = new Promise((resolve) => { releaseCall = resolve; });
  const manager = new McpClientManager(async (definition) => session({
    pages: [{ tools: [{ name: "search" }] }],
    onClose: () => {
      if (definition.url.hostname === "old.example") oldSessionClosed = true;
    },
    onCall: async () => {
      if (definition.url.hostname === "old.example") await callBlocked;
      return { content: [{ type: "text", text: definition.url.hostname }] };
    },
  }));
  const oldDefinition = connection("knowledge", "Old", "https://old.example/mcp");
  oldDefinition.allowedTools = ["search"];
  await manager.configure({ connections: [oldDefinition] });

  const activeCall = manager.call({ connectionId: "knowledge", toolName: "search", arguments: {} });
  const newDefinition = connection("knowledge", "New", "https://new.example/mcp");
  newDefinition.allowedTools = ["search"];
  await manager.configure({ connections: [newDefinition] });

  assert.equal(oldSessionClosed, false);
  releaseCall();
  assert.equal((await activeCall).text, "old.example");
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(oldSessionClosed, true);
  assert.equal((await manager.call({ connectionId: "knowledge", toolName: "search", arguments: {} })).text, "new.example");
  await manager.close();
});

test("MCP shutdown waits for an active call before closing its session", async () => {
  let releaseCall;
  let closed = false;
  const callBlocked = new Promise((resolve) => { releaseCall = resolve; });
  const manager = new McpClientManager(async () => session({
    pages: [{ tools: [{ name: "search" }] }],
    onCall: async () => {
      await callBlocked;
      return { content: [{ type: "text", text: "done" }] };
    },
    onClose: () => { closed = true; },
  }));
  const definition = connection("knowledge", "Knowledge", "https://knowledge.example/mcp");
  definition.allowedTools = ["search"];
  await manager.configure({ connections: [definition] });
  const activeCall = manager.call({ connectionId: "knowledge", toolName: "search", arguments: {} });

  let shutdownFinished = false;
  const shutdown = manager.close().then(() => { shutdownFinished = true; });
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(shutdownFinished, false);
  assert.equal(closed, false);

  releaseCall();
  await activeCall;
  await shutdown;
  assert.equal(closed, true);
});

function connection(id, displayName, url, enabled = true) {
  return { id, displayName, transport: "streamable_http", url, enabled, readOnly: true, allowedTools: [] };
}

function session({ pages, failPing = false, onClose = () => {}, onCall }) {
  return {
    async listTools(cursor) {
      if (cursor === undefined) return pages[0];
      if (cursor === "page-2") return pages[1];
      throw new Error("unexpected cursor");
    },
    async ping() {
      if (failPing) throw new Error("unhealthy");
    },
    async callTool(name, argumentsValue) {
      if (onCall) return await onCall(name, argumentsValue);
      return { content: [{ type: "text", text: `${name}:${argumentsValue.query ?? ""}` }] };
    },
    async close() {
      onClose();
    },
  };
}

function status(id, displayName, state, tools = []) {
  return { id, displayName, state, toolCount: tools.length, tools, allowedTools: [] };
}
