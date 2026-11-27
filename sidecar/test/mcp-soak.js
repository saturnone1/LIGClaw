import assert from "node:assert/strict";
import { McpClientManager } from "../dist/mcp/mcp-client-manager.js";

const iterations = Number.parseInt(process.argv[2] ?? "20", 10);
if (!Number.isInteger(iterations) || iterations < 1 || iterations > 200) throw new Error("iterations must be 1..200");
const before = process.memoryUsage().heapUsed;
for (let index = 0; index < iterations; index++) {
  const manager = new McpClientManager();
  const configured = await manager.configure({ connections: [{
    id: "sample-rag", displayName: "Sample RAG", transport: "stdio",
    url: "stdio://ligclaw-sample-rag", executableId: "ligclaw-sample-rag",
    enabled: true, readOnly: true, allowedTools: ["search_knowledge", "get_document"],
  }] });
  assert.equal(configured.connections[0].state, "connected");
  const result = await manager.call({
    connectionId: "sample-rag", toolName: "search_knowledge", arguments: { query: "Desktop" },
  });
  assert.match(result.text, /architecture/);
  await manager.close();
}
if (globalThis.gc) globalThis.gc();
const delta = process.memoryUsage().heapUsed - before;
assert.ok(delta < 64 * 1024 * 1024, `heap delta exceeded 64 MiB: ${delta}`);
process.stdout.write(JSON.stringify({ iterations, heapDeltaBytes: delta, result: "passed" }) + "\n");
