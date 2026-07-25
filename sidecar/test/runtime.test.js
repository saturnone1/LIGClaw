import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { ClineAgentRuntimeAdapter, createDeterministicSpikeModel } from "../dist/runtime/cline-agent-runtime-adapter.js";
import { DesktopToolBridge } from "../dist/runtime/desktop-tool-bridge.js";
import { RuntimeCoordinator } from "../dist/runtime/runtime-coordinator.js";

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
