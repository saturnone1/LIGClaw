import assert from "node:assert/strict";
import { test } from "node:test";
import {
  CapabilityProposalGenerationError,
  CapabilityProposalGenerator,
  FEATURE_BUILDER_SYSTEM_PROMPT,
} from "../dist/runtime/capability-proposal-generator.js";
import { ClineAgentRuntimeAdapter } from "../dist/runtime/cline-agent-runtime-adapter.js";

const NOW = Date.parse("2026-08-02T09:00:00.000Z");

function request(overrides = {}) {
  return {
    schemaVersion: 1,
    generationId: `fbgen_${"1".repeat(32)}`,
    requestHash: `sha256:${"2".repeat(64)}`,
    sessionId: `fbs_${"3".repeat(32)}`,
    sessionRevision: 4,
    inventoryHash: `sha256:${"4".repeat(64)}`,
    deadlineUtc: "2026-08-02T09:00:45.000Z",
    requirement: "지정 폴더의 파일을 유형별로 정리하는 기능을 추가해",
    answers: [],
    inventory: {
      schemaVersion: 1,
      tools: ["file.move.v1"],
      connectionKinds: [],
      capabilityIds: ["operations.files"],
    },
    ...overrides,
  };
}

function correlation(parameters) {
  return {
    schemaVersion: 1,
    generationId: parameters.generationId,
    requestHash: parameters.requestHash,
    sessionId: parameters.sessionId,
    sessionRevision: parameters.sessionRevision,
    inventoryHash: parameters.inventoryHash,
    deadlineUtc: parameters.deadlineUtc,
  };
}

function needsInformation(parameters, questions = [{
  questionId: "q_target",
  prompt: "정리할 대상 폴더는 어디인가요?",
  answerKind: "text",
}]) {
  return { ...correlation(parameters), outcome: "needs_information", questions };
}

function proposal(parameters, artifacts = [{
  path: "manifest.json",
  mediaType: "application/json",
  content: JSON.stringify({ schemaVersion: 1, id: "operations.sort-files", version: "1.0.0" }),
}]) {
  return { ...correlation(parameters), outcome: "proposal", artifacts, limitations: [] };
}

function createGenerator(responseFactory, observed = {}) {
  const model = {
    async *stream(modelRequest) {
      observed.calls = (observed.calls ?? 0) + 1;
      observed.tools = modelRequest.tools;
      observed.systemPrompt = modelRequest.systemPrompt;
      observed.userText = modelRequest.messages
        .filter((message) => message.role === "user")
        .flatMap((message) => message.content)
        .filter((part) => part.type === "text")
        .map((part) => part.text)
        .at(-1);
      const output = await responseFactory(modelRequest);
      yield { type: "text-delta", text: typeof output === "string" ? output : JSON.stringify(output) };
      yield { type: "finish", reason: "stop" };
    },
  };
  const adapter = new ClineAgentRuntimeAdapter(model);
  return new CapabilityProposalGenerator(
    (agentId, sessionId, systemPrompt) => adapter.createFeatureBuilderAgent(agentId, sessionId, systemPrompt),
    () => NOW,
  );
}

test("Feature Builder prompt bounds semantic v2 to no-effect Observation workflows", () => {
  assert.match(FEATURE_BUILDER_SYSTEM_PROMPT, /Prompt capabilities.*compatible v1 array.*accepted or rejected/i);
  assert.match(FEATURE_BUILDER_SYSTEM_PROMPT, /Workflow containing an Effect tool.*preparation mode is not None.*never label.*schemaVersion 2/i);
  assert.match(FEATURE_BUILDER_SYSTEM_PROMPT, /every Workflow tool\.call.*Observation.*preparation mode None.*semantic v2/i);
  assert.match(FEATURE_BUILDER_SYSTEM_PROMPT, /schemaVersion:2.*toolResults.*stepId.*arguments.*output.*visitedSteps/i);
  assert.match(FEATURE_BUILDER_SYSTEM_PROMPT, /1-16 cases.*at most 20 toolResults and 20 visitedSteps/i);
  assert.match(FEATURE_BUILDER_SYSTEM_PROMPT, /deterministic fake tool outputs.*no credentials or real effects.*fails closed/i);
});

test("Feature Builder returns one-to-three bounded questions from an ephemeral zero-tool Cline runtime", async () => {
  const parameters = request();
  const observed = {};
  const generator = createGenerator(() => needsInformation(parameters, [
    { questionId: "q_target", prompt: "대상 폴더는 어디인가요?", answerKind: "text" },
    {
      questionId: "q_rule",
      prompt: "정리 기준을 선택해 주세요.",
      answerKind: "single_choice",
      choices: ["확장자", "수정 날짜"],
    },
  ]), observed);

  const result = await generator.generate(parameters);

  assert.equal(result.outcome, "needs_information");
  assert.equal(result.questions.length, 2);
  assert.deepEqual(observed.tools, []);
  assert.match(observed.systemPrompt, /zero callable tools/i);
  assert.match(observed.userText, /<UNTRUSTED_REQUIREMENT_JSON>/);
  assert.doesNotMatch(observed.userText, /apiKey|provider.*secret/i);
});

test("Feature Builder returns bounded package artifacts without Desktop-owned proposal metadata", async () => {
  const parameters = request({ generationId: `fbgen_${"5".repeat(32)}` });
  const result = await createGenerator(() => proposal(parameters, [
    {
      path: "manifest.json",
      mediaType: "application/json",
      content: JSON.stringify({ schemaVersion: 1, id: "operations.sort-files", version: "1.0.0" }),
    },
    {
      path: "workflow.json",
      mediaType: "application/json",
      content: JSON.stringify({ schemaVersion: 1, steps: [{ tool: "file.move.v1" }] }),
    },
    {
      path: "instructions.md",
      mediaType: "text/markdown",
      content: "사용자가 지정한 규칙에 따라 typed file Tool을 사용합니다.",
    },
  ])).generate(parameters);

  assert.equal(result.outcome, "proposal");
  assert.deepEqual(result.artifacts.map((artifact) => artifact.path), [
    "manifest.json", "workflow.json", "instructions.md",
  ]);
  assert.equal("proposalId" in result, false);
  assert.equal("contentHash" in result, false);
});

test("Feature Builder rejects zero or four questions and every extra result property", async () => {
  for (const [suffix, questions] of [
    ["6", []],
    ["7", [0, 1, 2, 3].map((index) => ({
      questionId: `q_item_${index}`,
      prompt: `질문 ${index}`,
      answerKind: "text",
    }))],
  ]) {
    const parameters = request({ generationId: `fbgen_${suffix.repeat(32)}` });
    await assert.rejects(
      createGenerator(() => needsInformation(parameters, questions)).generate(parameters),
      (error) => error instanceof CapabilityProposalGenerationError &&
        error.code === "capability.proposal.invalid_response",
    );
  }

  const parameters = request({ generationId: `fbgen_${"8".repeat(32)}` });
  await assert.rejects(
    createGenerator(() => ({ ...proposal(parameters), activation: true })).generate(parameters),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );
});

test("Feature Builder rejects an oversized individual artifact and nested Desktop control metadata", async () => {
  const oversized = request({ generationId: `fbgen_${"9".repeat(32)}` });
  await assert.rejects(
    createGenerator(() => proposal(oversized, [{
      path: "instructions.md",
      mediaType: "text/markdown",
      content: "가".repeat(100_000),
    }])).generate(oversized),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );

  const metadata = request({ generationId: `fbgen_${"a".repeat(32)}` });
  await assert.rejects(
    createGenerator(() => proposal(metadata, [{
      path: "manifest.json",
      mediaType: "application/json",
      content: JSON.stringify({ id: "operations.sort-files", parent: "1.0.0" }),
    }])).generate(metadata),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );
});

test("Feature Builder enforces total response, artifact count, limitation count, and JSON depth bounds", async () => {
  const total = request({ generationId: `fbgen_${"e".repeat(32)}` });
  await assert.rejects(
    createGenerator(() => "x".repeat(1024 * 1024 + 1)).generate(total),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );

  const count = request({ generationId: `fbgen_${"f".repeat(32)}` });
  await assert.rejects(
    createGenerator(() => proposal(count, Array.from({ length: 7 }, (_, index) => ({
      path: "instructions.md",
      mediaType: "text/markdown",
      content: `artifact ${index}`,
    })))).generate(count),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );

  const limitations = request({
    generationId: `fbgen_${"0".repeat(32)}`,
    requestHash: `sha256:${"5".repeat(64)}`,
  });
  await assert.rejects(
    createGenerator(() => ({
      ...proposal(limitations),
      limitations: Array.from({ length: 17 }, (_, index) => `limitation ${index}`),
    })).generate(limitations),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );

  let nested = {};
  for (let index = 0; index < 26; index++) nested = { child: nested };
  const depth = request({
    generationId: `fbgen_${"1".repeat(31)}0`,
    requestHash: `sha256:${"6".repeat(64)}`,
  });
  await assert.rejects(
    createGenerator(() => proposal(depth, [{
      path: "workflow.json",
      mediaType: "application/json",
      content: JSON.stringify(nested),
    }])).generate(depth),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );
});

test("Feature Builder rejects any model correlation mismatch", async () => {
  const parameters = request({ generationId: `fbgen_${"b".repeat(32)}` });
  await assert.rejects(
    createGenerator(() => ({ ...needsInformation(parameters), inventoryHash: `sha256:${"f".repeat(64)}` }))
      .generate(parameters),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );
});

test("Feature Builder enforces the caller deadline with a hard sixty-second ceiling", async () => {
  const tooLong = request({
    generationId: `fbgen_${"3".repeat(31)}0`,
    requestHash: `sha256:${"d".repeat(64)}`,
    deadlineUtc: "2026-08-02T09:01:00.001Z",
  });
  assert.throws(
    () => createGenerator(() => needsInformation(tooLong)).generate(tooLong),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_request",
  );

  const expired = request({
    generationId: `fbgen_${"3".repeat(31)}1`,
    requestHash: `sha256:${"e".repeat(64)}`,
    deadlineUtc: "2026-08-02T09:00:00.000Z",
  });
  assert.throws(
    () => createGenerator(() => needsInformation(expired)).generate(expired),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.deadline_exceeded",
  );

  const timedOut = request({
    generationId: `fbgen_${"3".repeat(31)}2`,
    requestHash: `sha256:${"f".repeat(64)}`,
    deadlineUtc: "2026-08-02T09:00:00.001Z",
  });
  await assert.rejects(
    createGenerator(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
      return needsInformation(timedOut);
    }).generate(timedOut),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.deadline_exceeded",
  );
});

test("Feature Builder rejects at the deadline when the agent run never settles after abort", async () => {
  const parameters = request({
    generationId: `fbgen_${"3".repeat(31)}3`,
    requestHash: `sha256:${"a".repeat(64)}`,
    deadlineUtc: new Date(NOW + 15).toISOString(),
  });
  let abortCalls = 0;
  let unsubscribed = false;
  const generator = new CapabilityProposalGenerator(
    () => ({
      subscribe() {
        return () => { unsubscribed = true; };
      },
      abort() {
        abortCalls++;
      },
      run() {
        return new Promise(() => {});
      },
    }),
    () => NOW,
  );
  let watchdog;
  const startedAt = Date.now();

  try {
    await assert.rejects(
      Promise.race([
        generator.generate(parameters),
        new Promise((_, reject) => {
          watchdog = setTimeout(() => reject(new Error("deadline watchdog expired")), 500);
        }),
      ]),
      (error) => error instanceof CapabilityProposalGenerationError &&
        error.code === "capability.proposal.deadline_exceeded",
    );
  } finally {
    clearTimeout(watchdog);
  }

  assert.ok(Date.now() - startedAt < 250, "generation must reject independently of the stuck runtime");
  assert.equal(abortCalls, 1);
  assert.equal(unsubscribed, true);
});

test("Feature Builder rejects conversational secret collection on both sides of the boundary", async () => {
  const secretAnswer = request({
    generationId: `fbgen_${"2".repeat(31)}0`,
    requestHash: `sha256:${"7".repeat(64)}`,
    answers: [{ questionId: "q_connection", value: "Bearer abcdefghijklmnop" }],
  });
  const unused = createGenerator(() => needsInformation(secretAnswer));
  assert.throws(
    () => unused.generate(secretAnswer),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_request",
  );

  const prefixedSecret = request({
    generationId: `fbgen_${"2".repeat(31)}5`,
    requestHash: `sha256:${"c".repeat(64)}`,
    answers: [{ questionId: "q_connection", value: "sk-private-value-123456" }],
  });
  assert.throws(
    () => unused.generate(prefixedSecret),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_request",
  );

  const contextualAnswer = request({
    generationId: `fbgen_${"2".repeat(31)}1`,
    requestHash: `sha256:${"8".repeat(64)}`,
    answers: [{ questionId: "q_connection", value: "api key=private-value-123" }],
  });
  assert.throws(
    () => unused.generate(contextualAnswer),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_request",
  );

  const secretQuestion = request({
    generationId: `fbgen_${"2".repeat(31)}2`,
    requestHash: `sha256:${"9".repeat(64)}`,
  });
  await assert.rejects(
    createGenerator(() => needsInformation(secretQuestion, [{
      questionId: "q_connection_secret",
      prompt: "API key 값을 입력해 주세요.",
      answerKind: "text",
    }])).generate(secretQuestion),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );

  const secretChoice = request({
    generationId: `fbgen_${"2".repeat(31)}3`,
    requestHash: `sha256:${"a".repeat(64)}`,
  });
  await assert.rejects(
    createGenerator(() => needsInformation(secretChoice, [{
      questionId: "q_authentication",
      prompt: "인증 설정 방법을 선택해 주세요.",
      answerKind: "single_choice",
      choices: ["Desktop 보안 입력 화면 사용", "비밀번호 직접 입력"],
    }])).generate(secretChoice),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.invalid_response",
  );

  const authenticationKind = request({
    generationId: `fbgen_${"2".repeat(31)}4`,
    requestHash: `sha256:${"b".repeat(64)}`,
  });
  const allowed = await createGenerator(() => needsInformation(authenticationKind, [{
    questionId: "q_authentication",
    prompt: "연결 인증 방식은 무엇인가요?",
    answerKind: "single_choice",
    choices: ["Bearer", "None"],
  }])).generate(authenticationKind);
  assert.equal(allowed.outcome, "needs_information");
});

test("Feature Builder coalesces an exact duplicate and rejects generation-id tampering", async () => {
  const parameters = request({ generationId: `fbgen_${"c".repeat(32)}` });
  const observed = {};
  const generator = createGenerator(() => needsInformation(parameters), observed);

  const first = generator.generate(parameters);
  const duplicate = generator.generate(parameters);
  assert.strictEqual(duplicate, first);
  assert.deepEqual(await duplicate, await first);
  assert.equal(observed.calls, 1);

  assert.throws(
    () => generator.generate({ ...parameters, requestHash: `sha256:${"e".repeat(64)}` }),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.tamper_detected",
  );
  assert.throws(
    () => generator.generate({ ...parameters, requirement: "changed under the same request hash" }),
    (error) => error instanceof CapabilityProposalGenerationError &&
      error.code === "capability.proposal.tamper_detected",
  );
});

test("Feature Builder maps provider exceptions to a fixed code without raw detail", async () => {
  const parameters = request({ generationId: `fbgen_${"d".repeat(32)}` });
  const generator = createGenerator(() => {
    throw new Error("401 api_key=super-secret prompt=private-requirement");
  });

  await assert.rejects(generator.generate(parameters), (error) => {
    assert.equal(error.code, "capability.proposal.generation_failed");
    assert.equal(error.message, "capability.proposal.generation_failed");
    assert.doesNotMatch(String(error.stack), /super-secret|private-requirement/);
    return true;
  });
});
