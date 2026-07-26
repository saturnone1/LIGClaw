import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { BUILT_IN_PROVIDER } from "@cline/llms";
import {
  ClineAgentRuntimeAdapter,
  createDeterministicSpikeModel,
  OPENAI_COMPATIBLE_PROVIDER_ID,
  selectProviderRoute,
} from "../dist/runtime/cline-agent-runtime-adapter.js";
import { DesktopToolBridge } from "../dist/runtime/desktop-tool-bridge.js";
import { RuntimeCoordinator } from "../dist/runtime/runtime-coordinator.js";
import { classifyRuntimeFailure } from "../dist/runtime/runtime-failure.js";
import { detectTextToolCallFallback } from "../dist/runtime/text-tool-call-fallback.js";
import { detectMemoryLookupIntent, detectMemoryRememberIntent } from "../dist/runtime/memory-query-router.js";

test("runtime failures cross the process boundary as fixed codes without provider details", () => {
  const code = classifyRuntimeFailure("401 Unauthorized api_key=secret prompt=private-content");

  assert.equal(code, "runtime.provider_authentication");
  assert.doesNotMatch(code, /secret|private-content/);
  assert.equal(classifyRuntimeFailure('Unknown or disabled provider "openai".'), "runtime.configuration");
});

test("custom OpenAI endpoints use Cline's enabled compatible provider", () => {
  assert.equal(OPENAI_COMPATIBLE_PROVIDER_ID, BUILT_IN_PROVIDER.OPENAI_COMPATIBLE);
});

test("provider routing falls back only for an eligible fixed failure and records the reason", async () => {
  const routing = {
    primary: { profileId: "primary", baseUrl: "http://primary/v1", apiKey: "one", model: "model-a" },
    fallbacks: [{ profileId: "backup", baseUrl: "http://backup/v1", apiKey: "two", model: "model-b" }],
    allowFallback: true,
    explicitSelection: false,
  };
  const visited = [];
  const selected = await selectProviderRoute(routing, async (provider) => {
    visited.push(provider.profileId);
    return provider.profileId === "primary"
      ? { success: false, message: "요청 한도에 도달했습니다 (HTTP 429)." }
      : { success: true, message: "ok" };
  });

  assert.deepEqual(visited, ["primary", "backup"]);
  assert.equal(selected.provider.profileId, "backup");
  assert.equal(selected.transition, "backup|provider_rate_limited");
});

test("an explicitly selected profile never probes or silently falls back", async () => {
  let probes = 0;
  const selected = await selectProviderRoute({
    primary: { profileId: "chosen", baseUrl: "http://chosen/v1", apiKey: "one", model: "model-a" },
    fallbacks: [{ profileId: "backup", baseUrl: "http://backup/v1", apiKey: "two", model: "model-b" }],
    allowFallback: true,
    explicitSelection: true,
  }, async () => {
    probes++;
    return { success: false, message: "HTTP 429" };
  });

  assert.equal(probes, 0);
  assert.equal(selected.provider.profileId, "chosen");
  assert.equal(selected.transition, undefined);
});

test("Desktop bridge honors the timeout selected by a long-running tool", async () => {
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    setTimeout(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { completed: true },
    }), 30);
  });

  const result = await bridge.invoke({
    conversationId: "conversation-timeout",
    runId: "run-timeout",
    name: "file.copy.v1",
    risk: "R2",
    input: {},
  }, undefined, 100);

  assert.deepEqual(result, { completed: true });
});

test("Desktop bridge still rejects a tool that exceeds its selected timeout", async () => {
  const bridge = new DesktopToolBridge(() => {});

  await assert.rejects(bridge.invoke({
    conversationId: "conversation-timeout",
    runId: "run-timeout",
    name: "fixture.timeout.v1",
    risk: "R0",
    input: {},
  }, undefined, 5), /timed out/);
});

test("text-only JSON tool arguments are recovered through the approved Desktop bridge", async () => {
  const responses = new Map([
    ["내가 선호하는 에디터는 VS Code라고 기억해", {
      reason: "사용자가 선호하는 에디터를 기억해 달라고 요청함",
      kind: "preference", key: "preferred_editor", value: "VS Code", sensitivity: "general", ttlDays: 365,
    }],
    ["2분 뒤에 테스트 알림 보여줘", {
      reason: "사용자가 2분 뒤 테스트 알림을 요청함",
      title: "테스트 알림", message: "테스트 알림입니다.", delayMinutes: 2,
    }],
  ]);
  const textOnlyModel = {
    async *stream(request) {
      const userText = request.messages.at(-1)?.content?.find(part => part.type === "text")?.text;
      yield { type: "text-delta", text: JSON.stringify(responses.get(userText), null, 2) };
      yield { type: "finish", reason: "stop" };
    },
  };
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({ toolCallId: parameters.toolCallId, success: true, output: { completed: true } }));
  });
  const adapter = new ClineAgentRuntimeAdapter(textOnlyModel, bridge);

  for (const [index, input] of [...responses.keys()].entries()) {
    const events = [];
    await adapter.run(
      { conversationId: `conversation-json-${index}`, runId: `run-json-${index}`, input, runtime: "cline" },
      event => events.push(event),
      new AbortController().signal,
    );
    const output = events.filter(event => event.type === "text_delta").map(event => event.text).join("");
    assert.doesNotMatch(output, /^\s*\{/u);
    assert.equal(events.at(-1).type, "run_completed");
  }

  assert.equal(invocations[0].name, "memory.remember.v1");
  assert.equal(invocations[0].input.value, "VS Code");
  assert.equal(invocations[1].name, "schedule.create.v1");
  assert.equal(invocations[1].input.delayMinutes, 2);
  assert.equal(invocations[1].input.recurrence, "once");
  assert.equal(invocations[1].input.misfirePolicy, "run_once_on_resume");
});

test("cancelling a recovered JSON tool call remains cancelled", async () => {
  const model = {
    async *stream() {
      yield { type: "text-delta", text: JSON.stringify({
        appName: "Calculator",
        reason: "사용자가 계산기 실행을 요청했기 때문에",
      }) };
      yield { type: "finish", reason: "stop" };
    },
  };
  const cancellation = new AbortController();
  const bridge = new DesktopToolBridge(() => queueMicrotask(() => cancellation.abort("사용자 취소")));
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-cancel-fallback", runId: "run-cancel-fallback", input: "계산기 실행해 줘", runtime: "cline" },
    event => events.push(event),
    cancellation.signal,
  );

  assert.equal(events.at(-1).type, "run_cancelled");
  assert.equal(events.some(event => event.type === "run_completed"), false);
});

test("JSON fallback requires matching user intent and an exact safe argument shape", () => {
  const memoryJson = JSON.stringify({
    reason: "기억 요청", kind: "preference", key: "preferred_editor", value: "VS Code", sensitivity: "general",
  });
  assert.equal(detectTextToolCallFallback("JSON 예시를 보여줘", memoryJson), undefined);
  assert.equal(detectTextToolCallFallback("이 값을 기억해", JSON.stringify({
    reason: "기억 요청", kind: "preference", key: "preferred_editor", value: "VS Code", sensitivity: "general",
    unexpected: "not allowed",
  })), undefined);
});

test("explicit personal preference questions use Desktop memory lookup before the model", async () => {
  let modelCalls = 0;
  const model = {
    async *stream() {
      modelCalls++;
      yield { type: "text-delta", text: "모델의 잘못된 답변" };
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
      output: { memories: [{ key: "editor", value: "VS Code" }], truncated: false },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-memory-query", runId: "run-memory-query", input: "내 선호 에디터가 뭐야?", runtime: "cline" },
    event => events.push(event),
    new AbortController().signal,
  );

  assert.equal(modelCalls, 0);
  assert.equal(invocations.length, 1);
  assert.equal(invocations[0].name, "memory.list.v1");
  assert.equal(invocations[0].input.query, "editor");
  assert.match(events.filter(event => event.type === "text_delta").map(event => event.text).join(""), /VS Code/u);
  assert.equal(events.at(-1).type, "run_completed");
});

test("explicit preference writes always return a bounded answer without internal memory ids", async () => {
  let modelCalls = 0;
  const model = {
    async *stream() {
      modelCalls++;
      yield { type: "text-delta", text: "\n" };
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
      output: { memoryId: "must-not-be-shown", created: false },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-memory-write", runId: "run-memory-write", input: "선호 에디터는 VS Code라고 기억해", runtime: "cline" },
    event => events.push(event),
    new AbortController().signal,
  );

  const output = events.filter(event => event.type === "text_delta").map(event => event.text).join("");
  assert.equal(modelCalls, 0);
  assert.equal(invocations[0].name, "memory.remember.v1");
  assert.equal(invocations[0].input.key, "editor");
  assert.equal(invocations[0].input.value, "VS Code");
  assert.match(output, /업데이트했습니다/u);
  assert.doesNotMatch(output, /must-not-be-shown|메모리 ID/iu);
  assert.equal(events.at(-1).type, "run_completed");
});

test("completed model runs never leave a blank assistant response", async () => {
  const model = {
    async *stream() {
      yield { type: "text-delta", text: " \n\t" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-empty", runId: "run-empty", input: "응답해 줘", runtime: "cline" },
    event => events.push(event),
    new AbortController().signal,
  );

  const output = events.filter(event => event.type === "text_delta").map(event => event.text).join("");
  assert.match(output, /빈 응답/u);
  assert.equal(output.trim().length > 0, true);
  assert.equal(events.at(-1).type, "run_completed");
});

test("direct memory requests preserve the cached conversation session", async () => {
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push(request.messages.map(message => message.content
        .filter(part => part.type === "text").map(part => part.text).join("")));
      yield { type: "text-delta", text: "일반 답변" };
      yield { type: "finish", reason: "stop" };
    },
  };
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => queueMicrotask(() => bridge.complete({
    toolCallId: parameters.toolCallId,
    success: true,
    output: { created: true },
  })));
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);

  await adapter.run(
    { conversationId: "conversation-preserved", runId: "run-before-memory", input: "첫 질문", runtime: "cline" },
    () => {}, new AbortController().signal,
  );
  await adapter.run(
    { conversationId: "conversation-preserved", runId: "run-memory", input: "선호 IDE는 Rider라고 기억해", runtime: "cline" },
    () => {}, new AbortController().signal,
  );
  await adapter.run(
    { conversationId: "conversation-preserved", runId: "run-after-memory", input: "후속 질문", runtime: "cline" },
    () => {}, new AbortController().signal,
  );

  assert.equal(requests.length, 2);
  assert.deepEqual(requests[1], ["첫 질문", "일반 답변", "후속 질문"]);
});

test("system memory questions are never routed to personal memory", () => {
  assert.equal(detectMemoryLookupIntent("내 시스템 메모리 사용률이 뭐야?"), undefined);
  assert.deepEqual(detectMemoryLookupIntent("내 선호 에디터가 뭐야?"), {
    query: "editor",
    label: "선호 에디터",
  });
  assert.deepEqual(detectMemoryRememberIntent("선호 에디터는 Visual Studio라고 기억해"), {
    key: "editor",
    label: "선호 에디터",
    value: "Visual Studio",
  });
  assert.deepEqual(detectMemoryRememberIntent("선호 IDE는 Rider라고 기억해"), {
    key: "editor",
    label: "선호 에디터",
    value: "Rider",
  });
  assert.equal(detectMemoryLookupIntent("선호 에디터는 VS Code라고 기억해"), undefined);
});

test("real Cline agent loop maps deterministic model events", async () => {
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel());
  const events = [];

  await adapter.run(
    { conversationId: "conversation-1", runId: "run-1", input: "hello", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  assert.equal(events[0].type, "run_started");
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "요청을 확인했어요.\n\n“hello”\n\n현재는 대화 연결을 준비하는 단계예요. Windows 작업 기능이 연결되면 이 요청을 직접 처리할 수 있어요.",
  );
  assert.equal(events.at(-1).type, "run_completed");
});

test("Cline tool calls round-trip through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { windowsRelease: "Windows 10" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-tool", runId: "run-tool", input: "__test_system_status__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  assert.equal(invocations.length, 1);
  assert.equal(invocations[0].method, "tool.invoke");
  assert.equal(invocations[0].parameters.name, "system.get_status.v1");
  assert.equal(invocations[0].parameters.risk, "R0");
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "현재 운영체제는 Windows 10입니다.",
  );
  assert.equal(events.at(-1).type, "run_completed");
});

test("Cline reuses one conversation history across distinct Desktop runs", async () => {
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push(request.messages.map((message) => ({
        role: message.role,
        text: message.content.filter((part) => part.type === "text").map((part) => part.text).join(""),
      })));
      yield { type: "text-delta", text: `답변 ${requests.length}` };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);

  await adapter.run(
    { conversationId: "thread-1", runId: "run-1", input: "첫 질문", runtime: "cline" },
    () => {},
    new AbortController().signal,
  );
  await adapter.run(
    { conversationId: "thread-1", runId: "run-2", input: "후속 질문", runtime: "cline" },
    () => {},
    new AbortController().signal,
  );

  assert.deepEqual(requests[1].map(({ role, text }) => ({ role, text })), [
    { role: "user", text: "첫 질문" },
    { role: "assistant", text: "답변 1" },
    { role: "user", text: "후속 질문" },
  ]);
});

test("provider reconfiguration never aborts an active conversation and retires it after completion", async () => {
  const requests = [];
  let releaseFirstRequest;
  let firstRequestStarted;
  const firstRequestGate = new Promise((resolve) => { releaseFirstRequest = resolve; });
  const firstRequestEntered = new Promise((resolve) => { firstRequestStarted = resolve; });
  const model = {
    async *stream(request) {
      requests.push(request.messages.map((message) => message.content
        .filter((part) => part.type === "text").map((part) => part.text).join("")));
      if (requests.length === 1) {
        firstRequestStarted();
        await firstRequestGate;
      }
      yield { type: "text-delta", text: "완료" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);
  const firstEvents = [];
  const firstRun = adapter.run(
    { conversationId: "reconfigured-thread", runId: "run-before", input: "설정 변경 전 요청", runtime: "cline" },
    (event) => firstEvents.push(event),
    new AbortController().signal,
  );
  await firstRequestEntered;

  adapter.configure({ baseUrl: "https://example.invalid/v1", apiKey: "test-key", model: "replacement-model" });
  releaseFirstRequest();
  await firstRun;

  assert.equal(firstEvents.at(-1).type, "run_completed");
  assert.ok(firstEvents.every((event) => event.type !== "run_cancelled"));

  await adapter.run(
    { conversationId: "reconfigured-thread", runId: "run-after", input: "설정 변경 후 요청", runtime: "cline" },
    () => {},
    new AbortController().signal,
  );
  assert.deepEqual(requests[1], ["설정 변경 후 요청"]);
});

test("Cline never shares message history between conversation ids", async () => {
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push(request.messages.map((message) => message.content
        .filter((part) => part.type === "text").map((part) => part.text).join("")));
      yield { type: "text-delta", text: "답변" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);

  await adapter.run(
    { conversationId: "thread-a", runId: "run-a", input: "A의 비공개 문맥", runtime: "cline" },
    () => {}, new AbortController().signal,
  );
  await adapter.run(
    { conversationId: "thread-b", runId: "run-b", input: "B의 질문", runtime: "cline" },
    () => {}, new AbortController().signal,
  );

  assert.deepEqual(requests[1], ["B의 질문"]);
  assert.ok(requests[1].every((text) => !text.includes("A의 비공개 문맥")));
});

test("Desktop history snapshot replaces stale cached messages before a follow-up run", async () => {
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push(request.messages.map((message) => message.content
        .filter((part) => part.type === "text").map((part) => part.text).join("")));
      yield { type: "text-delta", text: "답변" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);
  await adapter.run(
    { conversationId: "bounded-thread", runId: "run-1", input: "캐시에만 있던 메시지", runtime: "cline" },
    () => {}, new AbortController().signal,
  );
  await adapter.run(
    { conversationId: "bounded-thread", runId: "run-2", input: "새 질문", runtime: "cline", history: [] },
    () => {}, new AbortController().signal,
  );

  assert.deepEqual(requests[1], ["새 질문"]);
});

test("Cline restores bounded Desktop history when a process-local session is absent", async () => {
  const requests = [];
  const model = {
    async *stream(request) {
      requests.push(request.messages);
      yield { type: "text-delta", text: "복원 완료" };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);

  await adapter.run({
    conversationId: "restored-thread",
    runId: "run-3",
    input: "이어서 질문",
    runtime: "cline",
    history: [
      { role: "user", content: "이전 질문" },
      { role: "assistant", content: "이전 답변" },
    ],
  }, () => {}, new AbortController().signal);

  assert.deepEqual(requests[0].map((message) => ({
    role: message.role,
    text: message.content.filter((part) => part.type === "text").map((part) => part.text).join(""),
  })), [
    { role: "user", text: "이전 질문" },
    { role: "assistant", text: "이전 답변" },
    { role: "user", text: "이어서 질문" },
  ]);
});

test("Cline rejects malformed or oversized restored history before contacting the model", async () => {
  let contacted = false;
  const adapter = new ClineAgentRuntimeAdapter({
    async *stream() {
      contacted = true;
      yield { type: "finish", reason: "stop" };
    },
  });

  await assert.rejects(adapter.run({
    conversationId: "invalid-history",
    runId: "invalid-run",
    input: "질문",
    runtime: "cline",
    history: [{ role: "system", content: "untrusted system prompt" }],
  }, () => {}, new AbortController().signal), /history is invalid/);
  assert.equal(contacted, false);
});

test("reused Cline session sends Tool calls under the current Desktop run id", async () => {
  let toolIndex = 0;
  const model = {
    async *stream(request) {
      if (request.messages.at(-1)?.role === "user") {
        yield {
          type: "tool-call-delta",
          toolCallId: `context-tool-${++toolIndex}`,
          toolName: "system_get_status",
          input: {},
        };
        yield { type: "finish", reason: "tool-calls" };
        return;
      }
      yield { type: "text-delta", text: "확인했습니다." };
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
      output: { windowsRelease: "Windows 11" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(model, bridge);

  await adapter.run(
    { conversationId: "tool-thread", runId: "tool-run-1", input: "첫 상태", runtime: "cline" },
    () => {}, new AbortController().signal,
  );
  await adapter.run(
    { conversationId: "tool-thread", runId: "tool-run-2", input: "다시 상태", runtime: "cline" },
    () => {}, new AbortController().signal,
  );

  assert.deepEqual(invocations.map((invocation) => invocation.runId), ["tool-run-1", "tool-run-2"]);
  assert.ok(invocations.every((invocation) => invocation.conversationId === "tool-thread"));
});

for (const observation of [
  {
    input: "__test_storage_status__",
    canonicalName: "system.get_storage_status.v1",
    output: { volumes: [], physicalHealthAvailable: false, healthNote: "logical only" },
  },
  {
    input: "__test_disk_health__",
    canonicalName: "system.get_disk_health.v1",
    output: { physicalDiskProviderStatus: "available", physicalDisks: [], bitLockerProviderStatus: "available", bitLockerVolumes: [] },
  },
  {
    input: "__test_security_status__",
    canonicalName: "system.get_security_status.v1",
    output: { defender: { status: "available" }, firewall: { status: "available" }, windowsUpdate: { status: "available" } },
  },
  {
    input: "__test_resource_status__",
    canonicalName: "system.get_resource_status.v1",
    output: { uptimeSeconds: 1, logicalProcessorCount: 4, cpuUsagePercent: 1, memoryTotalBytes: 2, memoryAvailableBytes: 1, memoryUsedPercent: 50 },
  },
  {
    input: "__test_network_status__",
    canonicalName: "system.get_network_status.v1",
    output: { networkAvailable: true, adapters: [] },
  },
]) {
  test(`Cline routes ${observation.canonicalName} through the Desktop bridge`, async () => {
    const invocations = [];
    let bridge;
    bridge = new DesktopToolBridge((method, parameters) => {
      invocations.push({ method, parameters });
      queueMicrotask(() => bridge.complete({
        toolCallId: parameters.toolCallId,
        success: true,
        output: observation.output,
      }));
    });
    const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
    const events = [];

    await adapter.run(
      { conversationId: `conversation-${observation.input}`, runId: `run-${observation.input}`, input: observation.input, runtime: "cline" },
      (event) => events.push(event),
      new AbortController().signal,
    );

    assert.equal(invocations.length, 1);
    assert.equal(invocations[0].method, "tool.invoke");
    assert.equal(invocations[0].parameters.name, observation.canonicalName);
    assert.equal(invocations[0].parameters.risk, "R0");
    assert.equal(events.at(-1).type, "run_completed");
  });
}

for (const web of [
  { input: "__test_web_fetch__", canonicalName: "web.fetch.v1", field: "url", value: "http://127.0.0.1:8080/document" },
  { input: "__test_web_search__", canonicalName: "web.search.v1", field: "query", value: "LIGClaw 정책" },
]) {
  test(`Cline routes ${web.canonicalName} through the Desktop R3 bridge`, async () => {
    const invocations = [];
    let bridge;
    bridge = new DesktopToolBridge((method, parameters) => {
      invocations.push({ method, parameters });
      queueMicrotask(() => bridge.complete({
        toolCallId: parameters.toolCallId,
        success: true,
        output: { text: "result", finalUrl: "http://127.0.0.1/result", truncated: false },
      }));
    });
    const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
    const events = [];

    await adapter.run(
      { conversationId: `conversation-${web.input}`, runId: `run-${web.input}`, input: web.input, runtime: "cline" },
      (event) => events.push(event),
      new AbortController().signal,
    );

    assert.equal(invocations.length, 1);
    assert.equal(invocations[0].method, "tool.invoke");
    assert.equal(invocations[0].parameters.name, web.canonicalName);
    assert.equal(invocations[0].parameters.risk, "R3");
    assert.equal(invocations[0].parameters.input[web.field], web.value);
    assert.equal(events.at(-1).type, "run_completed");
  });
}

for (const browser of [
  { input: "__test_browser_open__", canonicalName: "browser.open.v1", risk: "R3", field: "url", value: "http://127.0.0.1:8080/docs" },
  { input: "__test_browser_snapshot__", canonicalName: "browser.snapshot.v1", risk: "R1", field: "browserHandle", value: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
]) {
  test(`Cline routes ${browser.canonicalName} through the Desktop bridge`, async () => {
    const invocations = [];
    let bridge;
    bridge = new DesktopToolBridge((method, parameters) => {
      invocations.push({ method, parameters });
      queueMicrotask(() => bridge.complete({
        toolCallId: parameters.toolCallId, success: true,
        output: { browserHandle: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", elements: [] },
      }));
    });
    const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
    const events = [];

    await adapter.run(
      { conversationId: `conversation-${browser.input}`, runId: `run-${browser.input}`, input: browser.input, runtime: "cline" },
      (event) => events.push(event), new AbortController().signal,
    );

    assert.equal(invocations.length, 1);
    assert.equal(invocations[0].method, "tool.invoke");
    assert.equal(invocations[0].parameters.name, browser.canonicalName);
    assert.equal(invocations[0].parameters.risk, browser.risk);
    assert.equal(invocations[0].parameters.input[browser.field], browser.value);
    assert.equal(events.at(-1).type, "run_completed");
  });
}

test("Cline exposes only configured MCP tools through the Desktop R3 bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { text: "policy", source: "Knowledge", truncated: false },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  adapter.configureMcpTools([{ connectionId: "knowledge", toolName: "search" }]);
  const events = [];

  await adapter.run(
    { conversationId: "mcp-thread", runId: "mcp-run", input: "__test_mcp_read__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  assert.equal(invocations.length, 1);
  assert.equal(invocations[0].parameters.name, "mcp.read.v1");
  assert.equal(invocations[0].parameters.risk, "R3");
  assert.equal(invocations[0].parameters.input.toolName, "search");
  assert.equal(events.at(-1).type, "run_completed");
});

test("Cline lists visible windows through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: {
        windows: [
          { windowId: "0x1", title: "Inbox", processName: "mail", isForeground: true, isMinimized: false },
          { windowId: "0x2", title: "Notes", processName: "notepad", isForeground: false, isMinimized: true },
        ],
      },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-windows", runId: "run-windows", input: "__test_list_windows__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  assert.equal(invocations.length, 1);
  assert.equal(invocations[0].method, "tool.invoke");
  assert.equal(invocations[0].parameters.name, "app.list_windows.v1");
  assert.equal(invocations[0].parameters.risk, "R0");
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "열린 앱 창은 2개입니다.",
  );
  assert.equal(events.at(-1).type, "run_completed");
});

test("Cline searches installed app display names through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { apps: [{ displayName: "계산기" }], truncated: false },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-app-search", runId: "run-app-search", input: "__test_app_search__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.method, "tool.invoke");
  assert.equal(invocation.parameters.name, "app.search_installed.v1");
  assert.equal(invocation.parameters.risk, "R0");
  assert.deepEqual(invocation.parameters.input, { query: "계산" });
  assert.equal(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""), "설치 앱 1개를 찾았습니다.");
});

test("Cline requests approved Explorer context through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: {
        folderPath: "C:\\Work",
        selectedItems: [{ path: "C:\\Work\\report.docx", name: "report.docx", isDirectory: false }],
        selectionTruncated: false,
      },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-explorer", runId: "run-explorer", input: "__test_explorer_context__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.parameters.name, "explorer.get_context.v1");
  assert.equal(invocation.parameters.risk, "R1");
  assert.match(invocation.parameters.input.reason, /파일 탐색기/);
  assert.equal(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""), "파일 탐색기 선택 항목은 1개입니다.");
});

for (const fileInspection of [
  {
    input: "__test_file_metadata__",
    canonicalName: "file.get_metadata.v1",
    risk: "R0",
    output: { path: "C:\\Work\\report.txt", name: "report.txt", isDirectory: false, sizeBytes: 12, lastWriteTimeUtc: "2026-07-26T00:00:00Z", readOnly: false },
    expectedText: "파일 메타데이터를 확인했습니다.",
  },
  {
    input: "__test_file_read_text__",
    canonicalName: "file.read_text.v1",
    risk: "R1",
    output: { text: "report", length: 6, truncated: false },
    expectedText: "승인된 파일 텍스트를 읽었습니다.",
  },
]) {
  test(`Cline routes ${fileInspection.canonicalName} through the Desktop bridge`, async () => {
    const invocations = [];
    let bridge;
    bridge = new DesktopToolBridge((_method, parameters) => {
      invocations.push(parameters);
      queueMicrotask(() => bridge.complete({
        toolCallId: parameters.toolCallId,
        success: true,
        output: fileInspection.output,
      }));
    });
    const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
    const events = [];

    await adapter.run(
      { conversationId: `conversation-${fileInspection.input}`, runId: `run-${fileInspection.input}`, input: fileInspection.input, runtime: "cline" },
      (event) => events.push(event),
      new AbortController().signal,
    );

    const invocation = assertSingle(invocations);
    assert.equal(invocation.name, fileInspection.canonicalName);
    assert.equal(invocation.risk, fileInspection.risk);
    assert.equal(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""), fileInspection.expectedText);
  });
}

for (const mutation of [
  { input: "__test_file_create_directory__", canonicalName: "file.create_directory.v1", risk: "R1", expected: { path: "C:\\Work\\Reports", reason: "테스트 폴더 생성" } },
  { input: "__test_file_write_text__", canonicalName: "file.write_text.v1", risk: "R2", expected: { path: "C:\\Work\\report.txt", text: "report", reason: "테스트 파일 작성" } },
  { input: "__test_file_zip_create__", canonicalName: "file.zip_create.v1", risk: "R2", expected: { sourcePaths: ["C:\\Work\\a.txt"], destinationPath: "C:\\Work\\bundle.zip", reason: "테스트 압축" } },
  { input: "__test_file_zip_extract__", canonicalName: "file.zip_extract.v1", risk: "R2", expected: { zipPath: "C:\\Work\\bundle.zip", destinationPath: "C:\\Work\\Extracted", reason: "테스트 압축 해제" } },
]) {
  test(`Cline routes ${mutation.canonicalName} through the approved Desktop bridge`, async () => {
    const invocations = [];
    let bridge;
    bridge = new DesktopToolBridge((_method, parameters) => {
      invocations.push(parameters);
      queueMicrotask(() => bridge.complete({ toolCallId: parameters.toolCallId, success: true, output: { created: true } }));
    });
    const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
    await adapter.run({ conversationId: `conversation-${mutation.input}`, runId: `run-${mutation.input}`, input: mutation.input, runtime: "cline" }, () => {}, new AbortController().signal);
    const invocation = assertSingle(invocations);
    assert.equal(invocation.name, mutation.canonicalName);
    assert.equal(invocation.risk, mutation.risk);
    assert.deepEqual(invocation.input, mutation.expected);
  });
}

for (const action of [
  { input: "__test_app_set_window_state__", canonicalName: "app.set_window_state.v1", risk: "R1", expected: { windowId: "0x1234", state: "maximize", reason: "테스트 창 최대화" } },
  { input: "__test_system_open_settings__", canonicalName: "system.open_settings.v1", risk: "R1", expected: { page: "windows_update", reason: "테스트 설정 열기" } },
  { input: "__test_system_session_action__", canonicalName: "system.session_action.v1", risk: "R2", expected: { action: "lock", reason: "테스트 세션 잠금" } },
  { input: "__test_agent_job_control__", canonicalName: "agent_job.control.v1", risk: "R1", expected: { jobId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", action: "pause", reason: "테스트 작업 일시정지" } },
]) {
  test(`Cline routes ${action.canonicalName} through the approved Desktop bridge`, async () => {
    const invocations = [];
    let bridge;
    bridge = new DesktopToolBridge((_method, parameters) => {
      invocations.push(parameters);
      queueMicrotask(() => bridge.complete({ toolCallId: parameters.toolCallId, success: true, output: { applied: true } }));
    });
    const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
    const events = [];
    await adapter.run({ conversationId: `conversation-${action.input}`, runId: `run-${action.input}`, input: action.input, runtime: "cline" }, (event) => events.push(event), new AbortController().signal);
    const invocation = assertSingle(invocations);
    assert.equal(invocation.name, action.canonicalName);
    assert.equal(invocation.risk, action.risk);
    assert.deepEqual(invocation.input, action.expected);
    assert.equal(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""), "승인된 Windows 작업을 완료했습니다.");
  });
}

test("Cline requests an approved Windows notification through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { shown: true },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-notification", runId: "run-notification", input: "__test_show_notification__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.method, "tool.invoke");
  assert.equal(invocation.parameters.name, "system.show_notification.v1");
  assert.equal(invocation.parameters.risk, "R1");
  assert.deepEqual(invocation.parameters.input, {
    title: "LIGClaw 테스트",
    message: "승인된 알림 경로가 연결되었습니다.",
    reason: "Phase 1 승인 흐름을 검증하기 위해",
  });
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "승인한 알림을 표시했습니다.",
  );
});

test("Cline requests a registered app launch through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { launched: true, displayName: "Calculator" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-launch", runId: "run-launch", input: "__test_launch_app__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.method, "tool.invoke");
  assert.equal(invocation.parameters.name, "app.launch.v1");
  assert.equal(invocation.parameters.risk, "R1");
  assert.deepEqual(invocation.parameters.input, {
    appName: "Calculator",
    reason: "사용자가 계산기 실행을 요청했기 때문에",
  });
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "Calculator 앱을 실행했습니다.",
  );
});

test("Cline requests an R2 bounded file move through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((method, parameters) => {
    invocations.push({ method, parameters });
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { succeeded: 1, failed: 0, items: [{ name: "report.txt", success: true }] },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-move", runId: "run-move", input: "__test_move_file__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.parameters.name, "file.move.v1");
  assert.equal(invocation.parameters.risk, "R2");
  assert.deepEqual(invocation.parameters.input.sources, ["C:\\Phase2Fixture\\report.txt"]);
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "파일 1개를 이동했습니다.",
  );
});

test("Cline remembers only through the approved Desktop memory bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { memoryId: "a".repeat(32), created: true },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-memory", runId: "run-memory", input: "__test_memory_remember__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.name, "memory.remember.v1");
  assert.equal(invocation.risk, "R1");
  assert.equal(invocation.input.key, "주 프로젝트");
  assert.equal(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""), "승인한 내용을 기억했습니다.");
});

test("Cline creates a durable schedule only through the approved Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { jobId: "b".repeat(32), nextRunAtUtc: "2026-07-27T00:00:00Z", status: "pending" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-schedule", runId: "run-schedule", input: "__test_schedule_create__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.name, "schedule.create.v1");
  assert.equal(invocation.risk, "R1");
  assert.equal(invocation.input.recurrence, "weekly");
  assert.equal(invocation.input.timeZoneId, "Korea Standard Time");
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "승인한 반복 알림을 예약했습니다.",
  );
});

test("Cline forwards relative schedules without model-side time arithmetic or null interval", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { jobId: "c".repeat(32), nextRunAtUtc: "2026-07-26T08:10:00Z", status: "pending" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);

  await adapter.run(
    { conversationId: "conversation-relative", runId: "run-relative", input: "__test_schedule_relative__", runtime: "cline" },
    () => {},
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.deepEqual(invocation.input, {
    title: "테스트 알림",
    message: "테스트 시간이 되었습니다.",
    delayMinutes: 2,
    recurrence: "once",
    misfirePolicy: "run_once_on_resume",
    reason: "사용자가 2분 뒤 알림을 요청했기 때문에",
  });
});

test("Cline creates a bounded durable agent job only through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { jobId: "d".repeat(32), nextRunAtUtc: "2026-07-26T09:00:00Z", status: "pending" },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-agent-job", runId: "run-agent-job", input: "__test_agent_job_create__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.name, "agent_job.create.v1");
  assert.equal(invocation.risk, "R1");
  assert.equal(invocation.input.delayMinutes, 5);
  assert.equal(invocation.input.maxRuntimeSeconds, 180);
  assert.equal(invocation.input.maxAttempts, 2);
  assert.equal(events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "백그라운드 Agent 작업을 예약했습니다.");
});

test("Cline requests a bounded local subagent batch only through the Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: { batchId: "e".repeat(32), tasks: [], succeeded: 2, failed: 0 },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);

  await adapter.run(
    { conversationId: "conversation-subagents", runId: "run-subagents", input: "__test_subagent_run__", runtime: "cline" },
    () => {},
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.name, "subagent.run.v1");
  assert.equal(invocation.risk, "R1");
  assert.equal(invocation.input.tasks.length, 2);
  assert.equal(invocation.input.maxRisk, "R0");
  assert.equal(invocation.input.maxRuntimeSeconds, 120);
});

test("Cline inspects UIA only through the approved Desktop bridge", async () => {
  const invocations = [];
  let bridge;
  bridge = new DesktopToolBridge((_method, parameters) => {
    invocations.push(parameters);
    queueMicrotask(() => bridge.complete({
      toolCallId: parameters.toolCallId,
      success: true,
      output: {
        windowId: "0x123", title: "Fixture", processName: "fixture",
        elements: [{ elementId: "a".repeat(32), name: "저장", controlType: "Button" }], truncated: false,
      },
    }));
  });
  const adapter = new ClineAgentRuntimeAdapter(createDeterministicSpikeModel(), bridge);
  const events = [];

  await adapter.run(
    { conversationId: "conversation-uia", runId: "run-uia", input: "__test_uia_inspect__", runtime: "cline" },
    (event) => events.push(event),
    new AbortController().signal,
  );

  const invocation = assertSingle(invocations);
  assert.equal(invocation.name, "uia.inspect.v1");
  assert.equal(invocation.risk, "R1");
  assert.equal(invocation.input.windowId, "0x123");
  assert.equal(invocation.input.maxElements, 20);
  assert.equal(
    events.filter((event) => event.type === "text_delta").map((event) => event.text).join(""),
    "승인한 창에서 UI 요소를 확인했습니다.",
  );
});

function assertSingle(values) {
  assert.equal(values.length, 1);
  return values[0];
}

test("coordinator emits ordered normalized events", async () => {
  const fixture = JSON.parse(await readFile(
    new URL("../../tests/fixtures/replays/basic-agent-events.json", import.meta.url),
    "utf8",
  ));
  const adapter = {
    kind: "replay",
    async run(_request, emit) {
      emit({ type: "run_started" });
      emit({ type: "text_delta", text: "hello" });
      emit({ type: "run_completed" });
    },
  };
  const coordinator = new RuntimeCoordinator([adapter]);
  const events = [];
  const completed = new Promise((resolve) => {
    coordinator.start(
      { conversationId: "conversation-1", runId: "run-coordinator", input: "hello", runtime: "replay" },
      (event) => {
        events.push(event);
        if (event.type === "run_completed") resolve();
      },
    );
  });

  await completed;
  assert.deepEqual(
    events.map(({ sequence, type, text }) => ({ sequence, type, ...(text === undefined ? {} : { text }) })),
    fixture.events,
  );
  assert.ok(events.every((event) => event.runId === "run-coordinator"));
});

test("coordinator cancels an active run", async () => {
  const adapter = {
    kind: "replay",
    run(_request, _emit, signal) {
      return new Promise((_resolve, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      });
    },
  };
  const coordinator = new RuntimeCoordinator([adapter]);
  const terminal = new Promise((resolve) => {
    coordinator.start(
      { conversationId: "conversation-1", runId: "run-cancel", input: "hello", runtime: "replay" },
      (event) => {
        if (event.type === "run_cancelled") resolve(event);
      },
    );
  });

  assert.equal(coordinator.cancel("conversation-1").cancelled, true);
  const event = await terminal;
  assert.equal(event.type, "run_cancelled");
});

test("coordinator keeps parallel child conversations isolated by run id", async () => {
  const releases = new Map();
  const adapter = {
    kind: "replay",
    async run(request, emit) {
      emit({ type: "run_started" });
      await new Promise((resolve) => releases.set(request.runId, resolve));
      emit({ type: "text_delta", text: request.input });
      emit({ type: "run_completed" });
    },
  };
  const coordinator = new RuntimeCoordinator([adapter]);
  const events = [];
  coordinator.start(
    { conversationId: "child-1", runId: "child-run-1", input: "first", runtime: "replay" },
    (event) => events.push(event),
  );
  coordinator.start(
    { conversationId: "child-2", runId: "child-run-2", input: "second", runtime: "replay" },
    (event) => events.push(event),
  );
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(releases.size, 2);
  releases.get("child-run-2")();
  releases.get("child-run-1")();
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(
    events.filter((event) => event.type === "text_delta").map((event) => [event.runId, event.text]).sort(),
    [["child-run-1", "first"], ["child-run-2", "second"]],
  );
});

test("coordinator suppresses events after the first terminal event", async () => {
  const adapter = {
    kind: "replay",
    async run(_request, emit) {
      emit({ type: "run_started" });
      emit({ type: "run_completed" });
      emit({ type: "run_failed", message: "late failure" });
      throw new Error("failure after terminal");
    },
  };
  const coordinator = new RuntimeCoordinator([adapter]);
  const events = [];
  coordinator.start(
    { conversationId: "conversation-1", runId: "run-terminal", input: "hello", runtime: "replay" },
    (event) => events.push(event),
  );
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(events.map((event) => event.type), ["run_started", "run_completed"]);
});

test("coordinator rejects a duplicate active Desktop run id", () => {
  const adapter = {
    kind: "replay",
    run(_request, _emit, signal) {
      return new Promise((_resolve, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      });
    },
  };
  const coordinator = new RuntimeCoordinator([adapter]);
  coordinator.start(
    { conversationId: "conversation-1", runId: "desktop-run", input: "one", runtime: "replay" },
    () => {},
  );

  assert.throws(() => coordinator.start(
    { conversationId: "conversation-2", runId: "desktop-run", input: "two", runtime: "replay" },
    () => {},
  ), /already active/);
  coordinator.abortAll();
});
