import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";
import { createToolSchemaPairs } from "../../scripts/generate-contracts.mjs";
import {
  CONTRACT_HASH,
  TOOL_SCHEMA_REGISTRY,
  tryGetToolContractSchemas,
} from "../dist/generated/contracts.js";
import { BUILT_IN_TOOL_REGISTRATIONS } from "../dist/runtime/tools/tool-registration.js";

const canonicalTools = [
  "capability.builder.open.v1",
  "system.get_status.v1",
  "file.get_metadata.v1",
  "system.show_notification.v1",
  "mail.create_draft.v1",
  "mail.send.v1",
];

test("Builder-open schemas expose intent only and never carry the persisted requirement", () => {
  const schemas = tryGetToolContractSchemas("capability.builder.open.v1");
  assert.ok(schemas);
  const input = JSON.parse(schemas.canonicalInputSchemaJson);
  const result = JSON.parse(schemas.canonicalResultSchemaJson);

  assert.deepEqual(Object.keys(input.properties), ["intent"]);
  assert.deepEqual(input.required, ["intent"]);
  assert.deepEqual(input.properties.intent.enum, ["create", "revise"]);
  assert.equal(input.additionalProperties, false);
  assert.deepEqual(Object.keys(result.properties).sort(), ["accepted", "intent"]);
  assert.deepEqual([...result.required].sort(), ["accepted", "intent"]);
  assert.equal(result.properties.accepted.const, true);
  assert.deepEqual(result.properties.intent.enum, ["create", "revise"]);
  assert.equal(result.additionalProperties, false);
  for (const forbidden of ["requirement", "prompt", "userInput", "capabilityId", "content"])
    assert.equal(Object.hasOwn(input.properties, forbidden) || Object.hasOwn(result.properties, forbidden), false);
});

test("generated Tool schema registry pairs input and result by canonical file name", () => {
  assert.equal(CONTRACT_HASH, "7a221a94e96ef061630a92382356761cea49acdf271d0503a915c31ccd013776");
  assert.equal(Object.isFrozen(TOOL_SCHEMA_REGISTRY), true);
  for (const canonicalName of canonicalTools) {
    const schemas = tryGetToolContractSchemas(canonicalName);
    assert.ok(schemas);
    assert.equal(Object.isFrozen(schemas), true);
    assert.equal(schemas.canonicalName, canonicalName);
    assert.equal(
      JSON.parse(schemas.canonicalInputSchemaJson).$id,
      `https://ligclaw.local/contracts/tools/${canonicalName}.schema.json`,
    );
    assert.equal(
      JSON.parse(schemas.canonicalResultSchemaJson).$id,
      `https://ligclaw.local/contracts/tools/${canonicalName}.result.schema.json`,
    );
  }

  assert.equal(tryGetToolContractSchemas("SystemShowNotificationV1"), undefined);
  assert.equal(tryGetToolContractSchemas("toString"), undefined);
});

test("Tool schema pairing fails closed when either side is missing", () => {
  const toolsRoot = path.resolve("virtual-contracts", "tools");
  const input = schema(toolsRoot, "sample.observe.v1.schema.json", "MisleadingResultTitle");
  const result = schema(toolsRoot, "sample.observe.v1.result.schema.json", "MisleadingInputTitle");

  const pair = assertSingle(createToolSchemaPairs([input, result], toolsRoot));
  assert.equal(pair.canonicalName, "sample.observe.v1");
  assert.equal(pair.input, input);
  assert.equal(pair.result, result);
  assert.throws(
    () => createToolSchemaPairs([input], toolsRoot),
    /missing result schema: sample\.observe\.v1\.result\.schema\.json/,
  );
  assert.throws(
    () => createToolSchemaPairs([result], toolsRoot),
    /missing input schema: sample\.observe\.v1\.schema\.json/,
  );
});

test("mail draft schemas expose only bounded content and metadata and remain Workflow-only", () => {
  const schemas = tryGetToolContractSchemas("mail.create_draft.v1");
  assert.ok(schemas);
  const inputSchema = JSON.parse(schemas.canonicalInputSchemaJson);
  const resultSchema = JSON.parse(schemas.canonicalResultSchemaJson);

  assert.deepEqual(Object.keys(inputSchema.properties).sort(), ["bcc", "body", "cc", "subject", "to"]);
  assert.deepEqual([...inputSchema.required].sort(), ["body", "subject", "to"]);
  assert.deepEqual(
    Object.keys(resultSchema.properties).sort(),
    ["bodyLength", "draftHash", "ready", "recipientCount", "subjectLength"],
  );
  assert.deepEqual(
    [...resultSchema.required].sort(),
    ["bodyLength", "draftHash", "ready", "recipientCount", "subjectLength"],
  );
  assert.equal(resultSchema.properties.draftHash.minLength, 71);
  assert.equal(resultSchema.properties.draftHash.maxLength, 71);
  assert.equal(Object.hasOwn(resultSchema.properties.draftHash, "pattern"), false);

  const forbiddenFields = [
    "sender", "from", "endpoint", "url", "token", "auth", "header",
    "connectionSlot", "attachment", "html", "reason",
  ];
  for (const field of forbiddenFields) {
    assert.equal(Object.hasOwn(inputSchema.properties, field), false);
    assert.equal(Object.hasOwn(resultSchema.properties, field), false);
  }
  assert.equal(
    Object.values(BUILT_IN_TOOL_REGISTRATIONS)
      .some((registration) => registration.desktopName === "mail.create_draft.v1"),
    false,
  );

  const ajv = new Ajv2020({ allErrors: true, strict: true });
  ajv.addFormat("email", {
    type: "string",
    validate: (value) => /^[\x21-\x7e]+@[A-Za-z0-9.-]+$/.test(value),
  });
  const validateInput = ajv.compile(inputSchema);
  const validateResult = ajv.compile(resultSchema);
  const validInput = {
    to: ["owner@example.local"],
    cc: ["reviewer@example.local"],
    bcc: [],
    subject: "주간 보고서 초안",
    body: "검토할 보고서 초안입니다.",
  };
  assert.equal(validateInput(validInput), true, JSON.stringify(validateInput.errors));
  assert.equal(validateInput({ ...validInput, to: ["not-an-email"] }), false);
  assert.equal(validateInput({ ...validInput, to: ["owner@예시.내부"] }), false);
  assert.equal(validateInput({ ...validInput, to: ["owner@example.local", "owner@example.local"] }), false);
  assert.equal(validateInput({ ...validInput, to: Array.from({ length: 17 }, (_, index) => `u${index}@example.local`) }), false);
  assert.equal(validateInput({ ...validInput, cc: Array.from({ length: 17 }, (_, index) => `c${index}@example.local`) }), false);
  assert.equal(validateInput({ ...validInput, bcc: ["audit@example.local", "audit@example.local"] }), false);
  assert.equal(validateInput({ ...validInput, to: [`${"a".repeat(243)}@example.local`] }), false);
  assert.equal(validateInput({ ...validInput, subject: "" }), false);
  assert.equal(validateInput({ ...validInput, subject: "s".repeat(201) }), false);
  assert.equal(validateInput({ ...validInput, body: "" }), false);
  assert.equal(validateInput({ ...validInput, body: "b".repeat(8193) }), false);
  assert.equal(validateInput({ ...validInput, connectionSlot: "mail" }), false);
  assert.equal(validateInput({ subject: "제목", body: "본문" }), false);

  const validResult = {
    ready: true,
    draftHash: `sha256:${"a".repeat(64)}`,
    recipientCount: 2,
    subjectLength: 10,
    bodyLength: 20,
  };
  assert.equal(validateResult(validResult), true, JSON.stringify(validateResult.errors));
  assert.equal(validateResult({ ...validResult, ready: false }), false);
  assert.equal(validateResult({ ...validResult, draftHash: `sha256:${"a".repeat(63)}` }), false);
  assert.equal(validateResult({ ...validResult, draftHash: `sha256:${"a".repeat(65)}` }), false);
  assert.equal(validateResult({ ...validResult, recipientCount: 17 }), false);
  assert.equal(validateResult({ ...validResult, subjectLength: 201 }), false);
  assert.equal(validateResult({ ...validResult, bodyLength: 8193 }), false);
  assert.equal(validateResult({ ...validResult, token: "must-not-exist" }), false);
});

test("mail send schemas expose only bounded content and operation metadata and remain Workflow-only", () => {
  const schemas = tryGetToolContractSchemas("mail.send.v1");
  assert.ok(schemas);
  const inputSchema = JSON.parse(schemas.canonicalInputSchemaJson);
  const resultSchema = JSON.parse(schemas.canonicalResultSchemaJson);
  const expectedInputFields = ["bcc", "body", "cc", "connectionSlot", "draftHash", "subject", "to"];

  assert.deepEqual(Object.keys(inputSchema.properties).sort(), expectedInputFields);
  assert.deepEqual([...inputSchema.required].sort(), expectedInputFields);
  assert.deepEqual(Object.keys(resultSchema.properties).sort(), ["draftHash", "operationId", "status"]);
  assert.deepEqual([...resultSchema.required].sort(), ["draftHash", "operationId", "status"]);
  assert.equal(inputSchema.properties.draftHash.minLength, 71);
  assert.equal(inputSchema.properties.draftHash.maxLength, 71);
  assert.equal(Object.hasOwn(inputSchema.properties.draftHash, "pattern"), false);
  assert.equal(resultSchema.properties.operationId.minLength, 68);
  assert.equal(resultSchema.properties.operationId.maxLength, 68);
  assert.equal(Object.hasOwn(resultSchema.properties.operationId, "pattern"), false);
  assert.deepEqual(resultSchema.properties.status.enum, ["queued", "delivered"]);

  const forbiddenFields = [
    "endpoint", "url", "method", "path", "header", "auth", "token", "password", "secret",
    "sender", "from", "profileId", "operationId", "idempotencyKey", "retry", "reason", "html",
    "attachment",
  ];
  for (const field of forbiddenFields)
    assert.equal(Object.hasOwn(inputSchema.properties, field), false);
  assert.equal(
    Object.values(BUILT_IN_TOOL_REGISTRATIONS)
      .some((registration) => registration.desktopName === "mail.send.v1"),
    false,
  );

  const ajv = new Ajv2020({ allErrors: true, strict: true });
  ajv.addFormat("email", {
    type: "string",
    validate: (value) => /^[\x21-\x7e]+@[A-Za-z0-9.-]+$/.test(value),
  });
  const validateInput = ajv.compile(inputSchema);
  const validateResult = ajv.compile(resultSchema);
  const validInput = {
    connectionSlot: "mail",
    draftHash: `sha256:${"a".repeat(64)}`,
    to: ["owner@example.local"],
    cc: [],
    bcc: [],
    subject: "주간 보고서",
    body: "승인된 보고서 본문입니다.",
  };
  assert.equal(validateInput(validInput), true, JSON.stringify(validateInput.errors));
  assert.equal(validateInput({ ...validInput, connectionSlot: "" }), false);
  assert.equal(validateInput({ ...validInput, connectionSlot: "c".repeat(65) }), false);
  assert.equal(validateInput({ ...validInput, draftHash: `sha256:${"a".repeat(63)}` }), false);
  assert.equal(validateInput({ ...validInput, to: ["not-an-email"] }), false);
  assert.equal(validateInput({ ...validInput, to: ["owner@example.local", "owner@example.local"] }), false);
  assert.equal(validateInput({ ...validInput, to: Array.from({ length: 17 }, (_, index) => `u${index}@example.local`) }), false);
  assert.equal(validateInput({ ...validInput, cc: Array.from({ length: 17 }, (_, index) => `c${index}@example.local`) }), false);
  assert.equal(validateInput({ ...validInput, bcc: ["audit@example.local", "audit@example.local"] }), false);
  assert.equal(validateInput({ ...validInput, subject: "" }), false);
  assert.equal(validateInput({ ...validInput, subject: "s".repeat(201) }), false);
  assert.equal(validateInput({ ...validInput, body: "" }), false);
  assert.equal(validateInput({ ...validInput, body: "b".repeat(8193) }), false);
  assert.equal(validateInput({ ...validInput, endpoint: "https://must-not-exist.example" }), false);

  const validResult = {
    operationId: `mgo_${"a".repeat(64)}`,
    status: "queued",
    draftHash: `sha256:${"b".repeat(64)}`,
  };
  assert.equal(validateResult(validResult), true, JSON.stringify(validateResult.errors));
  assert.equal(validateResult({ ...validResult, status: "failed" }), false);
  assert.equal(validateResult({ ...validResult, operationId: `mgo_${"a".repeat(63)}` }), false);
  assert.equal(validateResult({ ...validResult, operationId: `mgo_${"a".repeat(65)}` }), false);
  assert.equal(validateResult({ ...validResult, draftHash: `sha256:${"b".repeat(65)}` }), false);
  assert.equal(validateResult({ ...validResult, retry: true }), false);
});

function schema(toolsRoot, fileName, title) {
  return {
    filePath: path.join(toolsRoot, fileName),
    value: {
      title,
      type: "object",
      additionalProperties: false,
      properties: {},
    },
  };
}

function assertSingle(values) {
  assert.equal(values.length, 1);
  return values[0];
}
