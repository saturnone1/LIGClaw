import assert from "node:assert/strict";
import { test } from "node:test";
import {
  CapabilityRunScope,
  computeCapabilityModelToolName,
  computeCapabilitySnapshotHash,
  createCapabilityAgentTools,
  createCapabilityRuntimeHooks,
  prepareCapabilityTurn,
  validateCapabilityRuntimeSnapshot,
} from "../dist/runtime/capability-runtime-projection.js";
import { ClineAgentRuntimeAdapter } from "../dist/runtime/cline-agent-runtime-adapter.js";
import { DesktopToolBridge } from "../dist/runtime/desktop-tool-bridge.js";

const FIXED_CONTENT_HASH = "sha256:4555f512a661bc53076cbbf3061c97c0d6dbe8f0e10d27be877867149ced0e03";
const FIXED_MODEL_TOOL_NAME = "cap_efda3ba7364eceba90e43816";
const FIXED_CONNECTION_BINDING_HASH = "sha256:32f802840a851f94529541365b5d775a76fb71da49fd588ab4cf061cc5c1b228";
const FIXED_SNAPSHOT_HASH = "sha256:ad429989009d2eb15c6888498a967b7ce01119a66ed27a48cdccd162bf40ee80";
const EMPTY_SNAPSHOT_HASH = "sha256:d5934746206037767ec6f085657325b28e7ea241c39a1dcf323bbdf113254758";

test("Sidecar hashing matches the fixed .NET Capability runtime vectors", () => {
  const candidate = fixedCandidate();

  assert.equal(computeCapabilityModelToolName(candidate), FIXED_MODEL_TOOL_NAME);
  assert.equal(computeCapabilitySnapshotHash([candidate]), FIXED_SNAPSHOT_HASH);
  assert.equal(computeCapabilitySnapshotHash([]), EMPTY_SNAPSHOT_HASH);
  assert.equal(validateCapabilityRuntimeSnapshot(snapshot([candidate])).snapshotHash, FIXED_SNAPSHOT_HASH);
  assert.equal(validateCapabilityRuntimeSnapshot(undefined).snapshotHash, EMPTY_SNAPSHOT_HASH);
  assert.equal(validateCapabilityRuntimeSnapshot(null).snapshotHash, EMPTY_SNAPSHOT_HASH);
});

test("Sidecar rejects tampered hashes, model names, duplicate IDs, and oversized snapshots", () => {
  const candidate = fixedCandidate();
  assert.throws(() => validateCapabilityRuntimeSnapshot({
    ...snapshot([candidate]),
    snapshotHash: `sha256:${"f".repeat(64)}`,
  }), /hash does not match/);

  const wrongModelName = { ...candidate, modelToolName: `cap_${"f".repeat(24)}` };
  assert.throws(() => validateCapabilityRuntimeSnapshot(snapshot([wrongModelName])), /model Tool name/);

  const duplicateId = candidateFor({ version: "2.0.0", contentHash: hash("b") });
  assert.throws(() => validateCapabilityRuntimeSnapshot(snapshot([candidate, duplicateId])), /duplicate ID/);

  const duplicateModelName = {
    ...candidateFor({ id: "company.sample.other", contentHash: hash("c") }),
    modelToolName: candidate.modelToolName,
  };
  assert.throws(() => validateCapabilityRuntimeSnapshot(snapshot([candidate, duplicateModelName])), /model Tool name/);

  const tooMany = Array.from({ length: 9 }, (_, index) => candidateFor({
    id: `company.sample.item${index}`,
    contentHash: `sha256:${index.toString(16).repeat(64)}`,
  }));
  assert.throws(() => validateCapabilityRuntimeSnapshot(snapshot(tooMany)), /snapshot is invalid/);

  const oversized = Array.from({ length: 8 }, (_, index) => candidateFor({
    id: `company.sample.large${index}`,
    contentHash: `sha256:${(index + 1).toString(16).repeat(64)}`,
    instructions: "\"".repeat(131_072),
  }));
  assert.throws(() => validateCapabilityRuntimeSnapshot(snapshot(oversized)), /snapshot is invalid/);
});

test("Capability input schema uniqueItems matches the bounded .NET subset", () => {
  const stringArray = {
    type: "object",
    additionalProperties: false,
    required: ["recipients"],
    properties: {
      recipients: {
        type: "array",
        minItems: 1,
        maxItems: 16,
        uniqueItems: true,
        items: { type: "string", format: "email", maxLength: 254 },
      },
    },
  };
  const accepted = candidateFor({ canonicalInputSchemaJson: JSON.stringify(stringArray) });
  const validated = validateCapabilityRuntimeSnapshot(snapshot([accepted]));
  assert.equal(validated.candidates[0].inputSchema.properties.recipients.uniqueItems, true);

  const falseOnIntegerArray = candidateFor({
    canonicalInputSchemaJson: JSON.stringify({
      type: "object",
      additionalProperties: false,
      properties: {
        values: {
          type: "array",
          maxItems: 16,
          uniqueItems: false,
          items: { type: "integer" },
        },
      },
    }),
  });
  assert.doesNotThrow(() => validateCapabilityRuntimeSnapshot(snapshot([falseOnIntegerArray])));

  for (const schema of [
    {
      ...stringArray,
      properties: {
        recipients: { ...stringArray.properties.recipients, uniqueItems: "true" },
      },
    },
    {
      ...stringArray,
      properties: {
        recipients: {
          type: "array",
          maxItems: 16,
          uniqueItems: true,
          items: { type: "integer" },
        },
      },
    },
    {
      ...stringArray,
      properties: {
        recipients: { type: "string", maxLength: 254, uniqueItems: false },
      },
    },
  ]) {
    const candidate = candidateFor({ canonicalInputSchemaJson: JSON.stringify(schema) });
    assert.throws(
      () => validateCapabilityRuntimeSnapshot(snapshot([candidate])),
      /input schema/,
    );
  }
});

test("selector bridge carries only exact control-plane identity and fixes one run selection", async () => {
  const validated = validateCapabilityRuntimeSnapshot(snapshot([fixedCandidate()]));
  const scope = new CapabilityRunScope(validated, "run-select");
  const invocations = [];
  const bridge = {
    async invoke(request) {
      invocations.push(request);
      return { selected: true, context: { ...request.input } };
    },
  };
  const tools = createCapabilityAgentTools(
    scope, bridge, "conversation-select", [fixtureTool("system_get_status")],
  );
  const selector = assertSingle(tools);
  const privateArguments = { reportBody: "MODEL-ARGUMENT-MUST-NOT-CROSS-CONTROL-PLANE" };

  const result = await selector.execute(privateArguments, {
    toolCallId: "selector-call",
    signal: new AbortController().signal,
  });

  const invocation = assertSingle(invocations);
  assert.equal(invocation.name, "capability.select.v1");
  assert.equal(invocation.risk, "R0");
  assert.deepEqual(Object.keys(invocation.input).sort(), [
    "capabilityId", "catalogRevision", "connectionBindingHash", "contentHash", "modelToolName",
    "snapshotHash", "version",
  ]);
  assert.doesNotMatch(JSON.stringify(invocation), /MODEL-ARGUMENT|현재 상태를 간결하게/);
  assert.deepEqual(result, { selected: true, context: invocation.input });
  assert.equal(scope.selected.modelToolName, FIXED_MODEL_TOOL_NAME);
  await assert.rejects(selector.execute({}, {}), /already selected/);
  assert.equal(invocations.length, 1);
});

test("mismatched Desktop selector acknowledgement never fixes Sidecar selection", async () => {
  const validated = validateCapabilityRuntimeSnapshot(snapshot([fixedCandidate()]));
  const scope = new CapabilityRunScope(validated, "run-stale-ack");
  const tools = createCapabilityAgentTools(scope, {
    async invoke(request) {
      return {
        selected: true,
        context: { ...request.input, catalogRevision: request.input.catalogRevision + 1 },
      };
    },
  }, "conversation-stale-ack", [fixtureTool("system_get_status")]);

  await assert.rejects(tools[0].execute({}, {}), /does not match the frozen snapshot/);
  assert.equal(scope.selected, undefined);
});

test("prepareTurn is provider-only and both Cline hooks enforce the selected Tool allowlist", async () => {
  const validated = validateCapabilityRuntimeSnapshot(snapshot([fixedCandidate()]));
  const scope = new CapabilityRunScope(validated, "run-hooks");
  const ordinaryTools = [fixtureTool("system_get_status"), fixtureTool("file_move")];
  const bridge = { invoke: async (request) => ({ selected: true, context: { ...request.input } }) };
  const capabilityTools = createCapabilityAgentTools(scope, bridge, "conversation-hooks", ordinaryTools);
  const hooks = createCapabilityRuntimeHooks(scope, ordinaryTools, capabilityTools);
  const allDefinitions = [...ordinaryTools, ...capabilityTools].map(({ name, description, inputSchema }) => ({
    name, description, inputSchema,
  }));

  assert.equal(hooks.beforeModel({ request: { tools: allDefinitions } }), undefined);
  assert.equal(hooks.beforeTool({ tool: ordinaryTools[1] }), undefined);
  assert.equal(prepareCapabilityTurn(scope, "base prompt"), undefined);

  await capabilityTools[0].execute({}, {});
  const filtered = hooks.beforeModel({ request: { tools: allDefinitions } });
  assert.deepEqual(filtered.tools.map((tool) => tool.name), ["system_get_status"]);
  assert.equal(hooks.beforeTool({ tool: ordinaryTools[0] }), undefined);
  assert.match(hooks.beforeTool({ tool: ordinaryTools[1] }).reason, /does not allow/);
  assert.match(hooks.beforeTool({ tool: capabilityTools[0] }).reason, /already selected/);

  const messages = Object.freeze([{ role: "user", content: [{ type: "text", text: "hello" }] }]);
  const prepared = prepareCapabilityTurn(scope, "base prompt");
  assert.match(prepared.systemPrompt, /현재 상태를 간결하게 설명합니다/);
  assert.equal("messages" in prepared, false);
  assert.doesNotMatch(JSON.stringify(messages), /현재 상태를 간결하게/);
});

test("an unavailable required Tool removes only that Capability from the model projection", () => {
  const unavailable = candidateFor({
    id: "company.sample.mcp",
    contentHash: hash("f"),
    requiredTools: ["mcp.read.v1"],
  });
  const available = fixedCandidate();
  const validated = validateCapabilityRuntimeSnapshot(snapshot([unavailable, available]));
  const scope = new CapabilityRunScope(validated, "run-unavailable-tool");
  const ordinaryTools = [fixtureTool("system_get_status")];
  const capabilityTools = createCapabilityAgentTools(
    scope,
    { invoke: async (request) => ({ selected: true, context: { ...request.input } }) },
    "conversation-unavailable-tool",
    ordinaryTools,
  );

  assert.deepEqual(capabilityTools.map((tool) => tool.name), [available.modelToolName]);
  assert.doesNotThrow(() => createCapabilityRuntimeHooks(scope, ordinaryTools, capabilityTools));
});

test("Cline selects a bounded Capability then invokes an allowed Desktop Tool with exact context", async () => {
  const candidate = fixedCandidate({
    canonicalInputSchemaJson: JSON.stringify({
      type: "object",
      additionalProperties: false,
      required: ["request"],
      properties: { request: { type: "string", maxLength: 160 } },
    }),
  });
  const capabilitySnapshot = snapshot([candidate]);
  const modelRequests = [];
  const model = selectionThenStatusModel(modelRequests, candidate);
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: parameters.name === "capability.select.v1"
        ? { selected: true, context: { ...parameters.input } }
        : { status: "ok" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const events = [];

  await adapter.run({
    conversationId: "conversation-capability",
    runId: "run-capability",
    input: "현재 상태를 기능으로 확인해 줘",
    runtime: "cline",
    capabilitySnapshot,
  }, (event) => events.push(event), new AbortController().signal);

  assert.deepEqual(invocations.map((invocation) => invocation.name), [
    "capability.select.v1", "system.get_status.v1",
  ]);
  const selector = invocations[0];
  assert.doesNotMatch(JSON.stringify(selector), /PRIVATE-MODEL-ARGUMENT|현재 상태를 간결하게/);
  assert.equal(selector.capabilityContext, undefined);
  assert.deepEqual(invocations[1].capabilityContext, selector.input);
  assert.equal(modelRequests[0].tools.some((tool) => tool.name === candidate.modelToolName), true);
  assert.deepEqual(modelRequests[1].tools.map((tool) => tool.name), ["system_get_status"]);
  assert.match(modelRequests[1].systemPrompt, /현재 상태를 간결하게 설명합니다/);
  assert.equal(modelRequests.every((request) =>
    !JSON.stringify(request.messages).includes("현재 상태를 간결하게 설명합니다.")), true);
  assert.equal(events.at(-1).type, "run_completed");
});

test("a matching Capability disables the early memory shortcut and carries exact context", async () => {
  const candidate = fixedCandidate({
    id: "company.sample.memory",
    contentHash: hash("9"),
    requiredTools: ["memory.remember.v1"],
  });
  const model = {
    async *stream(request) {
      const parts = request.messages.flatMap((message) => message.content);
      const selectorResult = parts.find((part) =>
        part.type === "tool-result" && part.toolName === candidate.modelToolName);
      const memoryResult = parts.find((part) =>
        part.type === "tool-result" && part.toolName === "memory_remember");
      if (!selectorResult) {
        yield {
          type: "tool-call-delta", toolCallId: "select-memory-capability",
          toolName: candidate.modelToolName, input: {},
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      if (!memoryResult) {
        yield {
          type: "tool-call-delta", toolCallId: "remember-after-selection", toolName: "memory_remember",
          input: {
            kind: "preference", key: "editor", value: "VS Code", sensitivity: "general",
            reason: "사용자가 명시적으로 기억해 달라고 요청했기 때문에",
          },
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "기억했습니다." };
      yield { type: "finish", reason: "stop" };
    },
  };
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: parameters.name === "capability.select.v1"
        ? { selected: true, context: { ...parameters.input } }
        : { created: true },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);

  await adapter.run({
    conversationId: "conversation-capability-memory",
    runId: "run-capability-memory",
    input: "선호 에디터는 VS Code라고 기억해",
    runtime: "cline",
    capabilitySnapshot: snapshot([candidate]),
  }, () => {}, new AbortController().signal);

  assert.deepEqual(invocations.map((invocation) => invocation.name), [
    "capability.select.v1", "memory.remember.v1",
  ]);
  assert.deepEqual(invocations[1].capabilityContext, invocations[0].input);
});

test("same-hash runs without Desktop history create a fresh Capability selection session", async () => {
  const candidate = fixedCandidate();
  const model = selectionThenStatusModel([], candidate);
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: parameters.name === "capability.select.v1"
        ? { selected: true, context: { ...parameters.input } }
        : { status: "ok" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const capabilitySnapshot = snapshot([candidate]);

  await adapter.run({
    conversationId: "conversation-no-history",
    runId: "run-no-history-1",
    input: "첫 실행",
    runtime: "cline",
    capabilitySnapshot,
  }, () => {}, new AbortController().signal);
  await adapter.run({
    conversationId: "conversation-no-history",
    runId: "run-no-history-2",
    input: "두 번째 실행",
    runtime: "cline",
    capabilitySnapshot,
  }, () => {}, new AbortController().signal);

  assert.deepEqual(invocations.map((invocation) => invocation.name), [
    "capability.select.v1", "system.get_status.v1",
    "capability.select.v1", "system.get_status.v1",
  ]);
  assert.equal(invocations[2].runId, "run-no-history-2");
  assert.deepEqual(invocations[3].capabilityContext, invocations[2].input);
});

test("same-turn selection cannot race a disallowed effect past the bridge allowlist", async () => {
  const candidate = fixedCandidate();
  const model = {
    async *stream(request) {
      const hasSelectorResult = request.messages.flatMap((message) => message.content)
        .some((part) => part.type === "tool-result" && part.toolName === candidate.modelToolName);
      if (!hasSelectorResult) {
        yield { type: "tool-call-delta", toolCallId: "select-first", toolName: candidate.modelToolName, input: {} };
        yield {
          type: "tool-call-delta",
          toolCallId: "disallowed-second",
          toolName: "file_move",
          input: { sources: ["C:\\Work\\a.txt"], destinationDirectory: "C:\\Archive", reason: "race test" },
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "selection fixed" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { selected: true, context: { ...parameters.input } },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);

  await adapter.run({
    conversationId: "conversation-race",
    runId: "run-race",
    input: "race",
    runtime: "cline",
    capabilitySnapshot: snapshot([candidate]),
  }, () => {}, new AbortController().signal);

  assert.deepEqual(invocations.map((invocation) => invocation.name), ["capability.select.v1"]);
});

test("JSON text fallback is disabled after an exact Capability selection", async () => {
  const candidate = fixedCandidate();
  const model = {
    async *stream(request) {
      const selected = request.messages.flatMap((message) => message.content)
        .some((part) => part.type === "tool-result" && part.toolName === candidate.modelToolName);
      if (!selected) {
        yield {
          type: "tool-call-delta", toolCallId: "select-before-fallback",
          toolName: candidate.modelToolName, input: {},
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield {
        type: "text-delta",
        text: JSON.stringify({ appName: "Calculator", reason: "텍스트 fallback 우회 회귀 테스트" }),
      };
      yield { type: "finish", reason: "stop" };
    },
  };
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { selected: true, context: { ...parameters.input } },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const events = [];

  await adapter.run({
    conversationId: "conversation-fallback",
    runId: "run-fallback",
    input: "계산기 열어 줘",
    runtime: "cline",
    capabilitySnapshot: snapshot([candidate]),
  }, (event) => events.push(event), new AbortController().signal);

  assert.deepEqual(invocations.map((invocation) => invocation.name), ["capability.select.v1"]);
  assert.match(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    /fallback을 실행할 수 없습니다/);
  assert.equal(events.at(-1).type, "run_completed");
});

test("hallucinated non-candidate selector never reaches Desktop", async () => {
  let calls = 0;
  const model = {
    async *stream(request) {
      const hasUnknownResult = request.messages.flatMap((message) => message.content)
        .some((part) => part.type === "tool-result" && part.toolName === `cap_${"f".repeat(24)}`);
      if (!hasUnknownResult) {
        yield {
          type: "tool-call-delta", toolCallId: "unknown-capability",
          toolName: `cap_${"f".repeat(24)}`, input: {},
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "rejected" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const bridge = new DesktopToolBridge(() => { calls++; });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);

  await adapter.run({
    conversationId: "conversation-hallucination",
    runId: "run-hallucination",
    input: "unknown",
    runtime: "cline",
    capabilitySnapshot: snapshot([fixedCandidate()]),
  }, () => {}, new AbortController().signal);

  assert.equal(calls, 0);
});

test("snapshot hash changes recreate the Cline session with bounded Desktop history", async () => {
  const first = candidateFor({ version: "1.0.0", contentHash: hash("1") });
  const second = candidateFor({ version: "2.0.0", contentHash: hash("2"), catalogRevision: 2 });
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push({
        tools: request.tools.map((tool) => tool.name),
        userTexts: request.messages.filter((message) => message.role === "user")
          .flatMap((message) => message.content)
          .filter((part) => part.type === "text")
          .map((part) => part.text),
      });
      yield { type: "text-delta", text: "ok" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const bridge = new DesktopToolBridge(() => {});
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);

  await adapter.run({
    conversationId: "conversation-recreate", runId: "run-one", input: "first", runtime: "cline",
    capabilitySnapshot: snapshot([first]),
  }, () => {}, new AbortController().signal);
  await adapter.run({
    conversationId: "conversation-recreate", runId: "run-two", input: "second", runtime: "cline",
    history: [{ role: "user", content: "bounded prior" }, { role: "assistant", content: "prior answer" }],
    capabilitySnapshot: snapshot([second]),
  }, () => {}, new AbortController().signal);

  assert.equal(requests[0].tools.includes(first.modelToolName), true);
  assert.equal(requests[0].tools.includes(second.modelToolName), false);
  assert.equal(requests[1].tools.includes(first.modelToolName), false);
  assert.equal(requests[1].tools.includes(second.modelToolName), true);
  assert.deepEqual(requests[1].userTexts, ["bounded prior", "second"]);
});

test("caller mutation during a run cannot change its frozen Capability projection", async () => {
  const rawCandidate = fixedCandidate();
  const rawSnapshot = snapshot([rawCandidate]);
  const originalToolName = rawCandidate.modelToolName;
  let releaseFirstTurn;
  let firstTurnStarted;
  const enteredFirstTurn = new Promise((resolve) => { firstTurnStarted = resolve; });
  const release = new Promise((resolve) => { releaseFirstTurn = resolve; });
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push(request);
      const selected = request.messages.flatMap((message) => message.content)
        .some((part) => part.type === "tool-result" && part.toolName === originalToolName);
      if (!selected) {
        firstTurnStarted();
        await release;
        yield { type: "tool-call-delta", toolCallId: "frozen-select", toolName: originalToolName, input: {} };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "frozen" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { selected: true, context: { ...parameters.input } },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const running = adapter.run({
    conversationId: "conversation-frozen",
    runId: "run-frozen",
    input: "freeze",
    runtime: "cline",
    capabilitySnapshot: rawSnapshot,
  }, () => {}, new AbortController().signal);

  await enteredFirstTurn;
  rawCandidate.instructions = "MUTATED-INSTRUCTIONS";
  rawCandidate.requiredTools[0] = "file.move.v1";
  rawCandidate.modelToolName = `cap_${"f".repeat(24)}`;
  rawSnapshot.snapshotHash = hash("f");
  releaseFirstTurn();
  await running;

  assert.equal(invocations[0].input.modelToolName, originalToolName);
  assert.deepEqual(requests[1].tools.map((tool) => tool.name), ["system_get_status"]);
  assert.match(requests[1].systemPrompt, /현재 상태를 간결하게 설명합니다/);
  assert.doesNotMatch(requests[1].systemPrompt, /MUTATED-INSTRUCTIONS/);
});

function selectionThenStatusModel(requests, candidate) {
  return {
    async *stream(request) {
      requests.push({
        systemPrompt: request.systemPrompt,
        tools: request.tools.map((tool) => ({ name: tool.name })),
        messages: request.messages,
      });
      const parts = request.messages.flatMap((message) => message.content);
      const selectorResult = parts.find((part) =>
        part.type === "tool-result" && part.toolName === candidate.modelToolName);
      const statusResult = parts.find((part) =>
        part.type === "tool-result" && part.toolName === "system_get_status");
      if (!selectorResult) {
        yield {
          type: "tool-call-delta",
          toolCallId: "select-capability",
          toolName: candidate.modelToolName,
          input: { request: "PRIVATE-MODEL-ARGUMENT" },
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      if (!statusResult) {
        yield {
          type: "tool-call-delta", toolCallId: "status-after-selection",
          toolName: "system_get_status", input: {},
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "상태를 확인했습니다." };
      yield { type: "finish", reason: "stop" };
    },
  };
}

function fixedCandidate(overrides = {}) {
  return candidateFor({
    id: "company.sample.status",
    version: "1.2.3",
    contentHash: FIXED_CONTENT_HASH,
    catalogRevision: 7,
    displayName: "Status Helper",
    description: "Explains the current status.",
    intentExamples: ["show current status"],
    instructions: "현재 상태를 간결하게 설명합니다.",
    canonicalInputSchemaJson: "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
    requiredTools: ["system.get_status.v1"],
    connectionBindingHash: FIXED_CONNECTION_BINDING_HASH,
    maximumRisk: "R1",
    ...overrides,
  });
}

function candidateFor(overrides = {}) {
  const candidate = {
    id: "company.sample.status",
    version: "1.0.0",
    contentHash: hash("a"),
    catalogRevision: 1,
    modelToolName: "",
    displayName: "Status Helper",
    description: "Explains the current status.",
    kind: "prompt",
    intentExamples: ["show current status"],
    instructions: "Use the selected Capability.",
    canonicalInputSchemaJson: "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
    requiredTools: ["system.get_status.v1"],
    connectionBindingHash: hash("d"),
    maximumRisk: "R1",
    ...overrides,
  };
  candidate.modelToolName = computeCapabilityModelToolName(candidate);
  return candidate;
}

function snapshot(candidates) {
  return { schemaVersion: 2, snapshotHash: computeCapabilitySnapshotHash(candidates), candidates };
}

function fixtureTool(name) {
  return { name, description: name, inputSchema: { type: "object" }, execute: async () => ({}) };
}

function hash(character) {
  return `sha256:${character.repeat(64)}`;
}

function assertSingle(values) {
  assert.equal(values.length, 1);
  return values[0];
}
