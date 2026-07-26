import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

const documents = [
  { id: "architecture", title: "LIGClaw architecture", text: "Desktop owns Windows effects, approval, secrets, scheduling, and SQLite. Sidecar owns the replaceable agent runtime and MCP sessions." },
  { id: "safety", title: "LIGClaw safety", text: "Destructive or external effects pass through the Desktop policy, preview, approval, execution, and audit pipeline." },
] as const;

const server = new Server(
  { name: "ligclaw-sample-rag", version: "1.0.0" },
  { capabilities: { tools: {} } },
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "search_knowledge",
      description: "Searches the bundled read-only LIGClaw example documents.",
      inputSchema: {
        type: "object",
        additionalProperties: false,
        required: ["query"],
        properties: { query: { type: "string", minLength: 1, maxLength: 200 } },
      },
      annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
    },
    {
      name: "get_document",
      description: "Reads one bundled example document by its stable id.",
      inputSchema: {
        type: "object",
        additionalProperties: false,
        required: ["id"],
        properties: { id: { type: "string", enum: documents.map((document) => document.id) } },
      },
      annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
    },
  ],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const args = request.params.arguments;
  if (request.params.name === "search_knowledge") {
    const query = typeof args?.query === "string" ? args.query.trim().toLocaleLowerCase("en-US") : "";
    if (!query || query.length > 200) return error("A bounded query is required.");
    const matches = documents
      .filter((document) => `${document.title}\n${document.text}`.toLocaleLowerCase("en-US").includes(query))
      .map(({ id, title }) => ({ id, title }));
    return text(JSON.stringify({ matches }));
  }
  if (request.params.name === "get_document") {
    const id = typeof args?.id === "string" ? args.id : "";
    const document = documents.find((candidate) => candidate.id === id);
    return document ? text(JSON.stringify(document)) : error("Document was not found.");
  }
  return error("Unknown tool.");
});

function text(value: string) {
  return { content: [{ type: "text" as const, text: value }] };
}

function error(value: string) {
  return { isError: true, content: [{ type: "text" as const, text: value }] };
}

await server.connect(new StdioServerTransport());
