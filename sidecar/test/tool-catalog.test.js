import assert from "node:assert/strict";
import test from "node:test";
import { createDesktopTools } from "../dist/runtime/tools/built-in-tool-catalog.js";
import {
  BUILT_IN_TOOL_CAPABILITIES,
  BUILT_IN_TOOL_REGISTRATIONS,
  TEXT_FALLBACK_AGENT_TOOL_NAMES,
  desktopToolName,
} from "../dist/runtime/tools/tool-registration.js";

const bridge = { invoke: async () => ({}) };

test("built-in Tool catalog has one unique registration for every exposed Tool", () => {
  const tools = createDesktopTools(bridge, "conversation", "run");
  const actualNames = tools.map(tool => tool.name);
  const registeredNames = Object.keys(BUILT_IN_TOOL_REGISTRATIONS);

  assert.equal(new Set(actualNames).size, actualNames.length);
  assert.deepEqual([...actualNames].sort(), [...registeredNames].sort());
  assert.equal(new Set(Object.values(BUILT_IN_TOOL_REGISTRATIONS).map(value => value.desktopName)).size,
    registeredNames.length);
  assert.deepEqual(BUILT_IN_TOOL_CAPABILITIES,
    Object.values(BUILT_IN_TOOL_REGISTRATIONS).map(value => `tool.${value.desktopName}`));
});

test("text fallback allowlist uses registrations explicitly marked for fallback", () => {
  const markedNames = Object.entries(BUILT_IN_TOOL_REGISTRATIONS)
    .filter(([, registration]) => registration.textFallback === true)
    .map(([name]) => name)
    .sort();

  assert.deepEqual([...TEXT_FALLBACK_AGENT_TOOL_NAMES].sort(), markedNames);
  assert.deepEqual(TEXT_FALLBACK_AGENT_TOOL_NAMES.map(desktopToolName).sort(), [
    "app.launch.v1",
    "memory.remember.v1",
    "schedule.create.v1",
  ]);
});

test("Feature Builder Tool forwards only a bounded intent and validates the Desktop acknowledgement", async () => {
  const invocations = [];
  const recordingBridge = {
    invoke: async (request) => {
      invocations.push(request);
      return { accepted: true, intent: request.input.intent };
    },
  };
  const tool = createDesktopTools(recordingBridge, "builder-conversation", "builder-run")
    .find(candidate => candidate.name === "capability_builder_open");
  assert.ok(tool);
  assert.deepEqual(tool.inputSchema, {
    type: "object",
    additionalProperties: false,
    required: ["intent"],
    properties: { intent: { type: "string", enum: ["create", "revise"] } },
  });

  const result = await tool.execute({ intent: "create" }, { toolCallId: "builder-call" });

  assert.deepEqual(result, { accepted: true, intent: "create" });
  assert.deepEqual(invocations, [{
    toolCallId: "builder-call",
    conversationId: "builder-conversation",
    runId: "builder-run",
    name: "capability.builder.open.v1",
    risk: "R0",
    input: { intent: "create" },
  }]);
  await assert.rejects(
    tool.execute(
      { intent: "revise", requirement: "MODEL_REQUIREMENT_MUST_NOT_CROSS" },
      { toolCallId: "injected-call" },
    ),
    /intent is invalid/,
  );
  assert.equal(invocations.length, 1);

  const reflectingTool = createDesktopTools({
    invoke: async request => ({
      accepted: true,
      intent: request.input.intent,
      requirement: "DESKTOP_REQUIREMENT_MUST_NOT_RETURN",
    }),
  }, "builder-conversation", "builder-run")
    .find(candidate => candidate.name === "capability_builder_open");
  assert.ok(reflectingTool);
  await assert.rejects(
    reflectingTool.execute({ intent: "revise" }, { toolCallId: "reflected-call" }),
    /invalid Feature Builder acknowledgement/,
  );
});
