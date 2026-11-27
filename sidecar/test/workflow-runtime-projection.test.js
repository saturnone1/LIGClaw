import assert from "node:assert/strict";
import Ajv2020 from "ajv/dist/2020.js";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import {
  CapabilityRunScope,
  computeCapabilityModelToolName,
  computeCapabilitySnapshotHash,
  createCapabilityAgentTools,
  createCapabilityRuntimeHooks,
  deriveWorkflowInvocationIdentity,
  validateCapabilityRuntimeSnapshot,
} from "../dist/runtime/capability-runtime-projection.js";
import { ClineAgentRuntimeAdapter } from "../dist/runtime/cline-agent-runtime-adapter.js";
import { DesktopToolBridge } from "../dist/runtime/desktop-tool-bridge.js";
import {
  WORKFLOW_AGENT_TOOL_TIMEOUT_MS,
  WORKFLOW_BRIDGE_TIMEOUT_MS,
  WORKFLOW_DESKTOP_DEADLINE_MS,
} from "../dist/runtime/runtime-policy.js";
import { workflowRuntimeCapabilities } from "../dist/runtime/runtime-capabilities.js";
import { DesktopWorkflowBridge } from "../dist/runtime/workflow-bridge.js";

const FIXED_NOW = Date.parse("2026-08-02T00:00:00.000Z");

test("Workflow projection selects exact context then invokes the dedicated bridge with bounded model input", async () => {
  const candidate = workflowCandidate();
  const validated = validateCapabilityRuntimeSnapshot(snapshot([candidate]));
  const scope = new CapabilityRunScope(validated, "run-workflow-direct");
  const selections = [];
  const workflowInvocations = [];
  const selectionBridge = {
    async invoke(request) {
      selections.push(request);
      return { selected: true, context: { ...request.input } };
    },
  };
  const workflowBridge = {
    async invoke(request, _signal, timeoutMs) {
      workflowInvocations.push({ request, timeoutMs });
      return {
        workflowRunId: "workflow-run-1",
        status: "succeeded",
        nextAction: "none",
        summary: "Workflow completed.",
        result: { notified: true },
      };
    },
  };
  const tools = createCapabilityAgentTools(
    scope,
    selectionBridge,
    "conversation-workflow-direct",
    [fixtureTool("system_get_status"), fixtureTool("file_get_metadata"), fixtureTool("system_show_notification")],
    workflowBridge,
    () => FIXED_NOW,
  );
  const tool = assertSingle(tools);
  const modelInput = { path: "C:\\reports\\weekly.txt", title: "Ready" };
  const execution = tool.execute(modelInput, {
    toolCallId: "cline-workflow-call-1",
    signal: new AbortController().signal,
  });
  modelInput.path = "C:\\tampered.txt";
  const result = await execution;

  const selection = assertSingle(selections);
  const workflow = assertSingle(workflowInvocations);
  assert.match(selection.toolCallId, /^wfsel_[0-9a-f]{48}$/);
  assert.match(workflow.request.workflowCallId, /^wfinv_[0-9a-f]{48}$/);
  assert.notEqual(selection.toolCallId, workflow.request.workflowCallId);
  assert.equal(selection.name, "capability.select.v1");
  assert.equal(workflow.request.name, "workflow.invoke.v1");
  assert.deepEqual(workflow.request.capabilityContext, selection.input);
  assert.deepEqual({ ...workflow.request.input }, { path: "C:\\reports\\weekly.txt", title: "Ready" });
  assert.equal(workflow.request.deadlineUtc, "2026-08-02T00:08:45.000Z");
  assert.match(workflow.request.idempotencyKey, /^wfid_[0-9a-f]{64}$/);
  assert.equal(workflow.timeoutMs, WORKFLOW_BRIDGE_TIMEOUT_MS);
  assert.equal(Object.isFrozen(workflow.request), true);
  assert.equal(Object.isFrozen(workflow.request.capabilityContext), true);
  assert.equal(Object.isFrozen(workflow.request.input), true);
  assert.doesNotMatch(JSON.stringify(workflow.request), /workflowJson|definition|steps|ticket|nonce/i);
  assert.equal(result.status, "succeeded");
  assert.equal(Object.isFrozen(result), true);
});

test("Workflow selection exposes zero ordinary Tools and denies every direct Tool path", async () => {
  const candidate = workflowCandidate();
  const validated = validateCapabilityRuntimeSnapshot(snapshot([candidate]));
  const scope = new CapabilityRunScope(validated, "run-workflow-hooks");
  const ordinaryTools = [fixtureTool("system_get_status"), fixtureTool("file_get_metadata")];
  const selectionBridge = { invoke: async (request) => ({ selected: true, context: { ...request.input } }) };
  const workflowBridge = {
    invoke: async () => ({ workflowRunId: "workflow-run-hooks", status: "succeeded", nextAction: "none" }),
  };
  const capabilityTools = createCapabilityAgentTools(
    scope,
    selectionBridge,
    "conversation-workflow-hooks",
    ordinaryTools,
    workflowBridge,
    () => FIXED_NOW,
  );
  const hooks = createCapabilityRuntimeHooks(scope, ordinaryTools, capabilityTools);
  const definitions = [...ordinaryTools, ...capabilityTools].map(({ name, description, inputSchema }) => ({
    name, description, inputSchema,
  }));

  await assertSingle(capabilityTools).execute(
    { path: "C:\\reports\\weekly.txt", title: "Ready" },
    { toolCallId: "cline-workflow-hooks" },
  );

  assert.deepEqual(hooks.beforeModel({ request: { tools: definitions } }).tools, []);
  assert.equal(hooks.beforeTool({ tool: ordinaryTools[0] }).reason, "capability.workflow.direct_tool_denied");
  assert.equal(hooks.beforeTool({ tool: capabilityTools[0] }).reason, "capability.workflow.direct_tool_denied");
  assert.throws(
    () => scope.assertDesktopToolAllowed("system.get_status.v1"),
    /capability\.workflow\.direct_tool_denied/,
  );
});

test("Workflow call identity and timeout ordering are deterministic and bounded", () => {
  const first = deriveWorkflowInvocationIdentity("cline-call", "conversation-a", "run-a");
  const replay = deriveWorkflowInvocationIdentity("cline-call", "conversation-a", "run-a");
  const anotherRun = deriveWorkflowInvocationIdentity("cline-call", "conversation-a", "run-b");

  assert.deepEqual(first, replay);
  assert.notEqual(first.selectionCallId, first.invocationCallId);
  assert.equal(first.selectionCallId.length, 54);
  assert.equal(first.invocationCallId.length, 54);
  assert.equal(first.idempotencyKey.length, 69);
  assert.notEqual(first.invocationCallId, anotherRun.invocationCallId);
  assert.equal(first.idempotencyKey, anotherRun.idempotencyKey);
  assert.throws(
    () => deriveWorkflowInvocationIdentity("x".repeat(2049), "conversation-a", "run-a"),
    /bounded model Tool call identity/,
  );
  assert.equal(WORKFLOW_DESKTOP_DEADLINE_MS < WORKFLOW_BRIDGE_TIMEOUT_MS, true);
  assert.equal(WORKFLOW_BRIDGE_TIMEOUT_MS < WORKFLOW_AGENT_TOOL_TIMEOUT_MS, true);
  assert.equal(30_000 + WORKFLOW_BRIDGE_TIMEOUT_MS < WORKFLOW_AGENT_TOOL_TIMEOUT_MS, true);
});

test("dedicated Workflow bridge correlates only Workflow results and fails closed on reuse or disconnect", async () => {
  const notifications = [];
  const bridge = new DesktopWorkflowBridge((method, parameters) => notifications.push({ method, parameters }));
  const request = workflowRequest("wfinv_" + "a".repeat(48));
  const pending = bridge.invoke(request, undefined, 5_000);
  const disconnected = assert.rejects(pending, /bridge disconnected/);

  assert.throws(() => bridge.invoke(request, undefined, 5_000), /Duplicate Workflow call id/);
  assert.equal(notifications.length, 1);
  assert.equal(notifications[0].method, "workflow.invoke");
  bridge.dispose();
  await disconnected;
  assert.equal(bridge.complete({
    workflowCallId: request.workflowCallId,
    success: true,
    output: { workflowRunId: "late", status: "succeeded", nextAction: "none" },
  }), false);
  await assert.rejects(bridge.invoke(workflowRequest("wfinv_" + "c".repeat(48))), /bridge disconnected/);

  const resultBridge = new DesktopWorkflowBridge(() => {});
  const deniedRequest = workflowRequest("wfinv_" + "b".repeat(48));
  const denied = resultBridge.invoke(deniedRequest, undefined, 5_000);
  assert.equal(resultBridge.complete({
    workflowCallId: deniedRequest.workflowCallId,
    success: false,
    error: "workflow.policy_denied",
  }), true);
  await assert.rejects(denied, /workflow\.policy_denied/);

  const invalidRequest = workflowRequest("wfinv_" + "d".repeat(48));
  const invalid = resultBridge.invoke(invalidRequest, undefined, 5_000);
  assert.equal(resultBridge.complete({
    workflowCallId: invalidRequest.workflowCallId,
    success: true,
    output: { workflowRunId: "invalid", status: "succeeded", nextAction: "none" },
    error: "workflow.must_not_coexist",
  }), true);
  await assert.rejects(invalid, /workflow\.bridge_invalid_result/);
});

test("Workflow projection rejects unbounded input and malformed or oversized structured results", async () => {
  const createTool = (workflowResult) => {
    const candidate = workflowCandidate();
    const scope = new CapabilityRunScope(validateCapabilityRuntimeSnapshot(snapshot([candidate])), "run-bounds");
    return assertSingle(createCapabilityAgentTools(
      scope,
      { invoke: async (request) => ({ selected: true, context: { ...request.input } }) },
      "conversation-bounds",
      [],
      { invoke: async () => workflowResult },
      () => FIXED_NOW,
    ));
  };
  const context = { toolCallId: "cline-bounds" };

  await assert.rejects(
    createTool({ workflowRunId: "run", status: "succeeded", nextAction: "none" })
      .execute({ path: "x".repeat(33_000), title: "Ready" }, context),
    /input is invalid or too large/,
  );
  const cyclic = { path: "C:\\report.txt", title: "Ready" };
  cyclic.self = cyclic;
  await assert.rejects(
    createTool({ workflowRunId: "run", status: "succeeded", nextAction: "none" }).execute(cyclic, context),
    /input is invalid or too large/,
  );
  await assert.rejects(
    createTool({ workflowRunId: "run", status: "succeeded", nextAction: "none", unexpected: true })
      .execute({ path: "C:\\report.txt", title: "Ready" }, context),
    /invalid Workflow result/,
  );
  const boundaryText = "x".repeat(100 * 1024 - Buffer.byteLength('{"text":""}', "utf8"));
  const boundaryResult = await createTool({
    workflowRunId: "run",
    status: "succeeded",
    nextAction: "none",
    result: { text: boundaryText },
  }).execute({ path: "C:\\report.txt", title: "Ready" }, context);
  assert.equal(boundaryResult.result.text.length, boundaryText.length);

  let depthBoundary = {};
  for (let index = 0; index < 16; index++) depthBoundary = { child: depthBoundary };
  const depthBoundaryResult = await createTool({
    workflowRunId: "run",
    status: "succeeded",
    nextAction: "none",
    result: depthBoundary,
  }).execute({ path: "C:\\report.txt", title: "Ready" }, context);
  assert.equal(Object.isFrozen(depthBoundaryResult.result), true);

  const nodeBoundary = {
    groups: Array.from({ length: 31 }, () => Array.from({ length: 128 }, () => 0)),
    tail: Array.from({ length: 94 }, () => 0),
  };
  const nodeBoundaryResult = await createTool({
    workflowRunId: "run",
    status: "succeeded",
    nextAction: "none",
    result: nodeBoundary,
  }).execute({ path: "C:\\report.txt", title: "Ready" }, context);
  assert.equal(nodeBoundaryResult.result.tail.length, 94);

  let excessiveDepth = {};
  for (let index = 0; index < 17; index++) excessiveDepth = { child: excessiveDepth };
  await assert.rejects(
    createTool({
      workflowRunId: "run",
      status: "succeeded",
      nextAction: "none",
      result: excessiveDepth,
    }).execute({ path: "C:\\report.txt", title: "Ready" }, context),
    /invalid Workflow result/,
  );
  await assert.rejects(
    createTool({
      workflowRunId: "run",
      status: "succeeded",
      nextAction: "none",
      result: { ...nodeBoundary, tail: Array.from({ length: 95 }, () => 0) },
    }).execute({ path: "C:\\report.txt", title: "Ready" }, context),
    /invalid Workflow result/,
  );
  await assert.rejects(
    createTool({
      workflowRunId: "run",
      status: "succeeded",
      nextAction: "none",
      result: { text: "x".repeat(101 * 1024) },
    }).execute({ path: "C:\\report.txt", title: "Ready" }, context),
    /invalid Workflow result/,
  );
  await assert.rejects(
    createTool({
      workflowRunId: "run",
      status: "needs_information",
      nextAction: "none",
      requiredInputs: ["/path"],
    }).execute({ path: "C:\\report.txt", title: "Ready" }, context),
    /invalid Workflow result/,
  );
});

test("real Cline loop sends Workflow through its dedicated bridge and the next model turn has zero Tools", async () => {
  const candidate = workflowCandidate();
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push({
        tools: request.tools.map((tool) => tool.name),
        systemPrompt: request.systemPrompt,
        messages: request.messages,
      });
      const result = request.messages.flatMap((message) => message.content)
        .find((part) => part.type === "tool-result" && part.toolName === candidate.modelToolName);
      if (!result) {
        yield {
          type: "tool-call-delta",
          toolCallId: "cline-workflow-e2e",
          toolName: candidate.modelToolName,
          input: { path: "C:\\reports\\weekly.txt", title: "Ready" },
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "워크플로를 완료했습니다." };
      yield { type: "finish", reason: "stop" };
    },
  };
  const toolNotifications = [];
  let toolBridge;
  toolBridge = new DesktopToolBridge((method, parameters) => {
    toolNotifications.push({ method, parameters });
    queueMicrotask(() => toolBridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { selected: true, context: { ...parameters.input } },
    }));
  });
  const workflowNotifications = [];
  let workflowBridge;
  workflowBridge = new DesktopWorkflowBridge((method, parameters) => {
    workflowNotifications.push({ method, parameters });
    queueMicrotask(() => workflowBridge.complete({
      workflowCallId: parameters.workflowCallId,
      success: true,
      output: {
        workflowRunId: "workflow-run-e2e",
        status: "succeeded",
        nextAction: "none",
        result: { notified: true },
      },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, toolBridge, undefined, workflowBridge);
  const events = [];

  await adapter.run({
    conversationId: "conversation-workflow-e2e",
    runId: "run-workflow-e2e",
    input: "보고서를 확인하고 알림을 보여줘",
    runtime: "cline",
    capabilitySnapshot: snapshot([candidate]),
  }, (event) => events.push(event), new AbortController().signal);

  assert.equal(requests[0].tools.includes(candidate.modelToolName), true);
  assert.deepEqual(requests[1].tools, []);
  assert.equal(toolNotifications.length, 1);
  assert.equal(toolNotifications[0].method, "tool.invoke");
  assert.equal(toolNotifications[0].parameters.name, "capability.select.v1");
  assert.equal(workflowNotifications.length, 1);
  assert.equal(workflowNotifications[0].method, "workflow.invoke");
  assert.equal(workflowNotifications[0].parameters.name, "workflow.invoke.v1");
  assert.deepEqual({ ...workflowNotifications[0].parameters.input }, {
    path: "C:\\reports\\weekly.txt",
    title: "Ready",
  });
  assert.notEqual(
    toolNotifications[0].parameters.toolCallId,
    workflowNotifications[0].parameters.workflowCallId,
  );
  assert.equal(events.at(-1).type, "run_completed");
});

test("Workflow capability is fail-closed without a dedicated bridge and handshake capability follows wiring", async () => {
  const candidate = workflowCandidate();
  const adapter = new ClineAgentRuntimeAdapter({
    async *stream() {
      throw new Error("model must not run");
    },
  }, new DesktopToolBridge(() => {}));

  await assert.rejects(adapter.run({
    conversationId: "conversation-unwired",
    runId: "run-unwired",
    input: "workflow",
    runtime: "cline",
    capabilitySnapshot: snapshot([candidate]),
  }, () => {}, new AbortController().signal), /dedicated Workflow bridge/);
  assert.deepEqual(workflowRuntimeCapabilities(false), []);
  assert.deepEqual(workflowRuntimeCapabilities(true), [
    "workflow.invoke", "workflow.result", "workflow.runtime.v1",
  ]);
});

test("schema-first Workflow control plane has no definition, step, or ticket payload", async () => {
  const candidateSchema = JSON.parse(await readFile(
    new URL("../../contracts/rpc/capability-runtime-candidate.schema.json", import.meta.url),
    "utf8",
  ));
  const invokeSchema = JSON.parse(await readFile(
    new URL("../../contracts/rpc/workflow-invoke-params.schema.json", import.meta.url),
    "utf8",
  ));
  const resultSchema = JSON.parse(await readFile(
    new URL("../../contracts/rpc/workflow-invoke-v1-result.schema.json", import.meta.url),
    "utf8",
  ));

  assert.deepEqual(candidateSchema.properties.kind.enum, ["prompt", "workflow"]);
  assert.deepEqual(Object.keys(invokeSchema.properties).sort(), [
    "capabilityContext", "conversationId", "deadlineUtc", "idempotencyKey", "input", "name", "runId",
    "workflowCallId",
  ]);
  assert.deepEqual(Object.keys(resultSchema.properties).sort(), [
    "errorCode", "nextAction", "requiredInputs", "result", "status", "summary", "workflowRunId",
  ]);
  const projectedPropertyNames = [
    ...Object.keys(candidateSchema.properties),
    ...Object.keys(invokeSchema.properties),
    ...Object.keys(resultSchema.properties),
  ];
  assert.equal(projectedPropertyNames.some((name) =>
    /workflowJson|definition|steps|ticket|nonce/i.test(name)), false);
});

test("Workflow invoke/result JSON Schemas reject envelope smuggling and inconsistent terminal states", async () => {
  const load = async (name) => JSON.parse(await readFile(
    new URL(`../../contracts/rpc/${name}`, import.meta.url),
    "utf8",
  ));
  const contextSchema = await load("capability-invocation-context.schema.json");
  const invokeSchema = await load("workflow-invoke-params.schema.json");
  const outputSchema = await load("workflow-invoke-v1-result.schema.json");
  const resultSchema = await load("workflow-result-params.schema.json");
  const ajv = new Ajv2020({ allErrors: true, strict: false });
  ajv.addFormat("date-time", (value) => Number.isFinite(Date.parse(value)) && /(?:Z|[+-]\d\d:\d\d)$/.test(value));
  ajv.addSchema(contextSchema);
  ajv.addSchema(outputSchema);
  const validateInvoke = ajv.compile(invokeSchema);
  const validateResult = ajv.compile(resultSchema);
  const request = workflowRequest("wfinv_" + "e".repeat(48));

  assert.equal(validateInvoke(request), true, JSON.stringify(validateInvoke.errors));
  assert.equal(validateInvoke({ ...request, workflowJson: "{}" }), false);
  assert.equal(validateInvoke({ ...request, workflowCallId: "cline-call" }), false);
  assert.equal(validateInvoke({ ...request, conversationId: "x".repeat(129) }), false);
  assert.equal(validateResult({
    workflowCallId: request.workflowCallId,
    success: true,
    output: { workflowRunId: "run", status: "succeeded", nextAction: "none" },
  }), true, JSON.stringify(validateResult.errors));
  assert.equal(validateResult({
    workflowCallId: request.workflowCallId,
    success: true,
    output: { workflowRunId: "run", status: "succeeded", nextAction: "retry_from_start" },
  }), false);
  assert.equal(validateResult({
    workflowCallId: request.workflowCallId,
    success: true,
    output: { workflowRunId: "run", status: "succeeded", nextAction: "none" },
    error: "workflow.must_not_coexist",
  }), false);
  assert.equal(validateResult({
    workflowCallId: request.workflowCallId,
    success: false,
    error: "workflow.policy_denied",
  }), true, JSON.stringify(validateResult.errors));
  assert.equal(validateResult({
    workflowCallId: request.workflowCallId,
    success: false,
    output: { workflowRunId: "run", status: "failed", nextAction: "retry_from_start" },
    error: "workflow.policy_denied",
  }), false);
  assert.equal(validateResult({
    workflowCallId: request.workflowCallId,
    success: true,
    output: { workflowRunId: "run", status: "needs_information", nextAction: "provide_information" },
  }), false);
});

function workflowCandidate(overrides = {}) {
  const candidate = {
    id: "company.sample.workflow",
    version: "1.0.0",
    contentHash: `sha256:${"a".repeat(64)}`,
    catalogRevision: 14,
    modelToolName: "",
    displayName: "Status report Workflow",
    description: "Checks a file and reports completion.",
    kind: "workflow",
    intentExamples: ["check the report and notify me"],
    instructions: "Summarize the bounded Desktop Workflow result.",
    canonicalInputSchemaJson: JSON.stringify({
      type: "object",
      additionalProperties: false,
      required: ["path", "title"],
      properties: {
        path: { type: "string", minLength: 3, maxLength: 4096 },
        title: { type: "string", minLength: 1, maxLength: 80 },
      },
    }),
    requiredTools: [
      "system.get_status.v1", "file.get_metadata.v1", "system.show_notification.v1",
    ],
    connectionBindingHash: `sha256:${"b".repeat(64)}`,
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

function workflowRequest(workflowCallId) {
  return {
    workflowCallId,
    conversationId: "conversation-bridge",
    runId: "run-bridge",
    name: "workflow.invoke.v1",
    capabilityContext: {
      snapshotHash: `sha256:${"1".repeat(64)}`,
      modelToolName: `cap_${"2".repeat(24)}`,
      capabilityId: "company.sample.workflow",
      version: "1.0.0",
      contentHash: `sha256:${"3".repeat(64)}`,
      catalogRevision: 1,
      connectionBindingHash: `sha256:${"5".repeat(64)}`,
    },
    idempotencyKey: `wfid_${"4".repeat(64)}`,
    deadlineUtc: "2026-08-02T00:08:45.000Z",
    input: { path: "C:\\report.txt", title: "Ready" },
  };
}

function assertSingle(values) {
  assert.equal(values.length, 1);
  return values[0];
}
