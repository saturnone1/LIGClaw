import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import Ajv2020 from "ajv/dist/2020.js";

const contractNames = [
  "capability-proposal-answer",
  "capability-proposal-artifact",
  "capability-proposal-inventory",
  "capability-proposal-question",
  "capability-proposal-generate-params",
  "capability-proposal-generate-result",
];
const schemas = await Promise.all(contractNames.map(async (name) => JSON.parse(await readFile(
  new URL(`../../contracts/rpc/${name}.schema.json`, import.meta.url),
  "utf8",
))));
const ajv = new Ajv2020({ allErrors: true, strict: true, formats: { "date-time": true } });
for (const schema of schemas.slice(0, -2)) ajv.addSchema(schema);
const validateParams = ajv.compile(schemas.at(-2));
const validateResult = ajv.compile(schemas.at(-1));

const common = {
  schemaVersion: 1,
  generationId: `fbgen_${"1".repeat(32)}`,
  requestHash: `sha256:${"2".repeat(64)}`,
  sessionId: `fbs_${"3".repeat(32)}`,
  sessionRevision: 1,
  inventoryHash: `sha256:${"4".repeat(64)}`,
  deadlineUtc: "2026-08-02T09:00:45.000Z",
};

test("Protocol 1.24 Capability proposal schemas enforce request and conditional outcome shapes", () => {
  assert.equal(validateParams({
    ...common,
    requirement: "파일 정리 기능",
    answers: [],
    inventory: { schemaVersion: 1, tools: ["file.move.v1"], connectionKinds: [], capabilityIds: [] },
  }), true, JSON.stringify(validateParams.errors));

  assert.equal(validateResult({
    ...common,
    outcome: "needs_information",
    questions: [{ questionId: "q_target", prompt: "대상은?", answerKind: "text" }],
  }), true, JSON.stringify(validateResult.errors));
  assert.equal(validateResult({
    ...common,
    outcome: "proposal",
    artifacts: [{
      path: "manifest.json",
      mediaType: "application/json",
      content: "{}",
    }],
    limitations: [],
  }), true, JSON.stringify(validateResult.errors));
  assert.equal(validateResult({ ...common, outcome: "needs_information", questions: [] }), false);
  assert.equal(validateResult({
    ...common,
    outcome: "needs_information",
    questions: [0, 1, 2, 3].map((item) => ({
      questionId: `q_${item}`,
      prompt: "질문",
      answerKind: "text",
    })),
  }), false);
  assert.equal(validateResult({
    ...common,
    outcome: "proposal",
    artifacts: [{ path: "manifest.json", mediaType: "application/json", content: "{}" }],
    limitations: [],
    proposalId: "sidecar-must-not-own-this",
  }), false);
  assert.equal(validateResult({
    ...common,
    outcome: "proposal",
    artifacts: [{
      path: "instructions.md",
      mediaType: "text/markdown",
      content: "x".repeat(262_145),
    }],
    limitations: [],
  }), false);
});
