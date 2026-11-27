import {
  type AgentBeforeModelResult,
  type AgentBeforeToolResult,
  type AgentRuntimeHooks,
  type AgentTool,
  type AgentToolDefinition,
} from "@cline/agents";
import { createHash, type Hash } from "node:crypto";
import type {
  CapabilityInvocationContext,
  CapabilityRuntimeCandidate,
  CapabilityRuntimeSnapshot,
  CapabilitySelectV1Result,
  WorkflowInvokeParams,
  WorkflowInvokeV1Result,
} from "../generated/contracts.js";
import type { DesktopToolInvoker } from "./tools/tool-shared.js";
import { BUILT_IN_TOOL_REGISTRATIONS } from "./tools/tool-registration.js";
import type { WorkflowInvoker } from "./workflow-bridge.js";
import {
  WORKFLOW_AGENT_TOOL_TIMEOUT_MS,
  WORKFLOW_BRIDGE_TIMEOUT_MS,
  WORKFLOW_DESKTOP_DEADLINE_MS,
} from "./runtime-policy.js";

const MODEL_TOOL_FORMAT = "capability-model-tool-v1";
const SNAPSHOT_FORMAT = "capability-runtime-snapshot-v2";
const SNAPSHOT_SCHEMA_VERSION = 2;
const MAXIMUM_CANDIDATES = 8;
const MAXIMUM_INTENT_EXAMPLES = 16;
const MAXIMUM_REQUIRED_TOOLS = 32;
const MAXIMUM_INSTRUCTIONS_CHARACTERS = 131_072;
const MAXIMUM_SCHEMA_CHARACTERS = 131_072;
const MAXIMUM_SCHEMA_DEPTH = 8;
const MAXIMUM_SCHEMA_PROPERTIES = 128;
const MAXIMUM_SERIALIZED_SNAPSHOT_BYTES = 2 * 1024 * 1024;
const CAPABILITY_SELECTION_TIMEOUT_MS = 30_000;
const MAXIMUM_WORKFLOW_INPUT_BYTES = 32 * 1024;
const MAXIMUM_WORKFLOW_FINAL_OUTPUT_BYTES = 100 * 1024;
const MAXIMUM_WORKFLOW_RESULT_ENVELOPE_BYTES = MAXIMUM_WORKFLOW_FINAL_OUTPUT_BYTES + 32 * 1024;
const MAXIMUM_MODEL_TOOL_CALL_ID_BYTES = 2 * 1024;
const MAXIMUM_RUNTIME_VALUE_DEPTH = 16;
const MAXIMUM_RUNTIME_VALUE_NODES = 4_096;
const MAXIMUM_WORKFLOW_RESULT_ENVELOPE_DEPTH = MAXIMUM_RUNTIME_VALUE_DEPTH + 1;
const MAXIMUM_WORKFLOW_RESULT_ENVELOPE_NODES = MAXIMUM_RUNTIME_VALUE_NODES + 64;
const MAXIMUM_RUNTIME_COLLECTION_ITEMS = 128;
const MAXIMUM_RUNTIME_PROPERTY_NAME_CHARACTERS = 256;
const HASH_PATTERN = /^sha256:[0-9a-f]{64}$/;
const MODEL_TOOL_PATTERN = /^cap_[0-9a-f]{24}$/;
const WORKFLOW_ERROR_CODE_PATTERN = /^[a-z][a-z0-9]*(?:[._][a-z0-9]+)+$/;
const WORKFLOW_RESULT_KEYS = new Set([
  "workflowRunId", "status", "nextAction", "summary", "requiredInputs", "result", "errorCode",
]);
const WORKFLOW_STATUSES = new Set([
  "succeeded", "needs_information", "failed", "cancelled", "attention_required",
]);
const WORKFLOW_NEXT_ACTIONS = new Set([
  "none", "provide_information", "retry_from_start", "review_outcome",
]);
const WORKFLOW_NEXT_ACTION_BY_STATUS = Object.freeze({
  succeeded: "none",
  needs_information: "provide_information",
  failed: "retry_from_start",
  cancelled: "retry_from_start",
  attention_required: "review_outcome",
} as const);
if (!(WORKFLOW_DESKTOP_DEADLINE_MS < WORKFLOW_BRIDGE_TIMEOUT_MS &&
      CAPABILITY_SELECTION_TIMEOUT_MS + WORKFLOW_BRIDGE_TIMEOUT_MS < WORKFLOW_AGENT_TOOL_TIMEOUT_MS))
  throw new Error("Workflow timeout ordering is invalid.");
const CAPABILITY_ID_PATTERN = /^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?){1,7}$/;
const SEMANTIC_VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;
const RISK_VALUES = new Set(["R0", "R1", "R2", "R3", "R4"]);
const CANDIDATE_KEYS = new Set([
  "id", "version", "contentHash", "catalogRevision", "modelToolName", "displayName", "description",
  "kind", "intentExamples", "instructions", "canonicalInputSchemaJson", "requiredTools",
  "connectionBindingHash", "maximumRisk",
]);
const SNAPSHOT_KEYS = new Set(["schemaVersion", "snapshotHash", "candidates"]);
const CONTEXT_KEYS = new Set([
  "snapshotHash", "modelToolName", "capabilityId", "version", "contentHash", "catalogRevision",
  "connectionBindingHash",
]);
const SCHEMA_KEYWORDS = new Set([
  "$schema", "type", "title", "description", "properties", "required", "additionalProperties",
  "items", "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "minimum", "maximum",
  "exclusiveMinimum", "exclusiveMaximum", "enum", "const", "format",
]);
const SCHEMA_TYPES = new Set(["object", "array", "string", "integer", "number", "boolean"]);
const SCHEMA_FORMATS = new Set(["date", "date-time", "email", "uuid"]);

interface AgentToolExecutionContext {
  readonly toolCallId?: string;
  readonly signal?: AbortSignal;
}

interface AgentBeforeModelContext {
  readonly request: { readonly tools: readonly AgentToolDefinition[] };
}

interface AgentBeforeToolContext {
  readonly tool: AgentTool;
}

export interface ValidatedCapabilityRuntimeCandidate extends CapabilityRuntimeCandidate {
  readonly inputSchema: Readonly<Record<string, unknown>>;
}

export interface ValidatedCapabilityRuntimeSnapshot extends CapabilityRuntimeSnapshot {
  readonly candidates: readonly ValidatedCapabilityRuntimeCandidate[];
}

export class CapabilityRunScope {
  private selectedCandidate: ValidatedCapabilityRuntimeCandidate | undefined;
  private pendingModelToolName: string | undefined;

  constructor(
    readonly snapshot: ValidatedCapabilityRuntimeSnapshot,
    public runId: string,
  ) {}

  get selected(): ValidatedCapabilityRuntimeCandidate | undefined {
    return this.selectedCandidate;
  }

  get invocationContext(): CapabilityInvocationContext | undefined {
    return this.selectedCandidate
      ? createInvocationContext(this.snapshot.snapshotHash, this.selectedCandidate)
      : undefined;
  }

  reset(runId: string): void {
    this.runId = runId;
    this.selectedCandidate = undefined;
    this.pendingModelToolName = undefined;
  }

  beginSelection(candidate: ValidatedCapabilityRuntimeCandidate): void {
    if (!this.snapshot.candidates.includes(candidate))
      throw new Error("Capability selection is outside the frozen run snapshot.");
    if (this.selectedCandidate || this.pendingModelToolName)
      throw new Error("A Capability is already selected for this run.");
    this.pendingModelToolName = candidate.modelToolName;
  }

  commitSelection(candidate: ValidatedCapabilityRuntimeCandidate): void {
    if (this.pendingModelToolName !== candidate.modelToolName || this.selectedCandidate)
      throw new Error("Capability selection state is invalid.");
    this.selectedCandidate = candidate;
    this.pendingModelToolName = undefined;
  }

  abandonSelection(candidate: ValidatedCapabilityRuntimeCandidate): void {
    if (this.pendingModelToolName === candidate.modelToolName)
      this.pendingModelToolName = undefined;
  }

  assertDesktopToolAllowed(canonicalToolName: string): void {
    if (this.selectedCandidate?.kind === "workflow")
      throw new Error("capability.workflow.direct_tool_denied");
    if (this.selectedCandidate && !this.selectedCandidate.requiredTools.includes(canonicalToolName))
      throw new Error("The selected Capability does not allow this Tool.");
  }
}

export function validateCapabilityRuntimeSnapshot(
  value: CapabilityRuntimeSnapshot | null | undefined,
): ValidatedCapabilityRuntimeSnapshot {
  if (value === undefined || value === null) return createValidatedSnapshot([]);
  if (!isRecord(value) || !hasExactKeys(value, SNAPSHOT_KEYS) || value.schemaVersion !== SNAPSHOT_SCHEMA_VERSION ||
      typeof value.snapshotHash !== "string" || !HASH_PATTERN.test(value.snapshotHash) ||
      !Array.isArray(value.candidates) || value.candidates.length > MAXIMUM_CANDIDATES ||
      serializedSnapshotExceedsBudget(value))
    throw new Error("Capability runtime snapshot is invalid.");

  const candidates = value.candidates.map(validateCandidate);
  const ids = new Set<string>();
  const modelToolNames = new Set<string>();
  for (const candidate of candidates) {
    if (ids.has(candidate.id)) throw new Error("Capability runtime snapshot contains a duplicate ID.");
    ids.add(candidate.id);
    if (modelToolNames.has(candidate.modelToolName))
      throw new Error("Capability runtime snapshot contains a model Tool collision.");
    modelToolNames.add(candidate.modelToolName);
  }
  const computedHash = computeCapabilitySnapshotHash(candidates);
  if (computedHash !== value.snapshotHash)
    throw new Error("Capability runtime snapshot hash does not match its contents.");
  return freezeSnapshot({ schemaVersion: SNAPSHOT_SCHEMA_VERSION, snapshotHash: computedHash, candidates });
}

export function computeCapabilityModelToolName(
  candidate: Pick<CapabilityRuntimeCandidate, "id" | "version" | "contentHash">,
): string {
  const hash = createHash("sha256");
  append(hash, "format", MODEL_TOOL_FORMAT);
  append(hash, "capabilityId", candidate.id);
  append(hash, "version", candidate.version);
  append(hash, "contentHash", candidate.contentHash);
  return `cap_${hash.digest("hex").slice(0, 24)}`;
}

export function computeCapabilitySnapshotHash(
  candidates: readonly CapabilityRuntimeCandidate[],
): string {
  const hash = createHash("sha256");
  append(hash, "format", SNAPSHOT_FORMAT);
  append(hash, "schemaVersion", String(SNAPSHOT_SCHEMA_VERSION));
  append(hash, "candidateCount", String(candidates.length));
  for (const [index, candidate] of candidates.entries()) {
    const prefix = `candidates[${index}]`;
    append(hash, `${prefix}.id`, candidate.id);
    append(hash, `${prefix}.version`, candidate.version);
    append(hash, `${prefix}.contentHash`, candidate.contentHash);
    append(hash, `${prefix}.catalogRevision`, String(candidate.catalogRevision));
    append(hash, `${prefix}.modelToolName`, candidate.modelToolName);
    append(hash, `${prefix}.displayName`, candidate.displayName);
    append(hash, `${prefix}.description`, candidate.description);
    append(hash, `${prefix}.kind`, candidate.kind);
    appendList(hash, `${prefix}.intentExamples`, candidate.intentExamples);
    append(hash, `${prefix}.instructions`, candidate.instructions);
    append(hash, `${prefix}.canonicalInputSchemaJson`, candidate.canonicalInputSchemaJson);
    appendList(hash, `${prefix}.requiredTools`, candidate.requiredTools);
    append(hash, `${prefix}.connectionBindingHash`, candidate.connectionBindingHash);
    append(hash, `${prefix}.maximumRisk`, candidate.maximumRisk);
  }
  return `sha256:${hash.digest("hex")}`;
}

export function createCapabilityAgentTools(
  scope: CapabilityRunScope,
  bridge: DesktopToolInvoker,
  conversationId: string,
  ordinaryTools: readonly AgentTool[],
  workflowBridge?: WorkflowInvoker,
  clock: () => number = Date.now,
): readonly AgentTool[] {
  const availableCanonicalNames = new Set(canonicalToolMap(ordinaryTools).values());
  return scope.snapshot.candidates
    .filter((candidate) => candidate.kind === "workflow"
      ? workflowBridge !== undefined
      : candidate.requiredTools.every((name) => availableCanonicalNames.has(name)))
    .map((candidate) => candidate.kind === "workflow"
      ? createWorkflowCapabilityAgentTool(scope, bridge, workflowBridge!, conversationId, candidate, clock)
      : createPromptCapabilityAgentTool(scope, bridge, conversationId, candidate));
}

function createPromptCapabilityAgentTool(
  scope: CapabilityRunScope,
  bridge: DesktopToolInvoker,
  conversationId: string,
  candidate: ValidatedCapabilityRuntimeCandidate,
): AgentTool {
  return {
    name: candidate.modelToolName,
    description: capabilityToolDescription(candidate),
    inputSchema: candidate.inputSchema,
    timeoutMs: CAPABILITY_SELECTION_TIMEOUT_MS,
    retryable: false,
    async execute(_input: unknown, context: AgentToolExecutionContext) {
      scope.beginSelection(candidate);
      const expectedContext = createInvocationContext(scope.snapshot.snapshotHash, candidate);
      try {
        const output = await bridge.invoke({
          ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
          conversationId,
          runId: scope.runId,
          name: "capability.select.v1",
          risk: "R0",
          input: { ...expectedContext },
        }, context.signal, CAPABILITY_SELECTION_TIMEOUT_MS);
        const acknowledgement = validateSelectionAcknowledgement(output, expectedContext);
        scope.commitSelection(candidate);
        return acknowledgement;
      } catch (error) {
        scope.abandonSelection(candidate);
        throw error;
      }
    },
  } satisfies AgentTool<unknown, CapabilitySelectV1Result>;
}

function createWorkflowCapabilityAgentTool(
  scope: CapabilityRunScope,
  selectionBridge: DesktopToolInvoker,
  workflowBridge: WorkflowInvoker,
  conversationId: string,
  candidate: ValidatedCapabilityRuntimeCandidate,
  clock: () => number,
): AgentTool {
  return {
    name: candidate.modelToolName,
    description: capabilityToolDescription(candidate),
    inputSchema: candidate.inputSchema,
    timeoutMs: WORKFLOW_AGENT_TOOL_TIMEOUT_MS,
    retryable: false,
    async execute(input: unknown, context: AgentToolExecutionContext) {
      const boundedInput = cloneBoundedWorkflowInput(input);
      const identity = deriveWorkflowInvocationIdentity(
        context.toolCallId,
        conversationId,
        scope.runId,
      );
      const expectedContext = createInvocationContext(scope.snapshot.snapshotHash, candidate);
      scope.beginSelection(candidate);
      try {
        const output = await selectionBridge.invoke({
          toolCallId: identity.selectionCallId,
          conversationId,
          runId: scope.runId,
          name: "capability.select.v1",
          risk: "R0",
          input: { ...expectedContext },
        }, context.signal, CAPABILITY_SELECTION_TIMEOUT_MS);
        validateSelectionAcknowledgement(output, expectedContext);
        scope.commitSelection(candidate);
      } catch (error) {
        scope.abandonSelection(candidate);
        throw error;
      }

      const now = clock();
      if (!Number.isSafeInteger(now) || now < 0 ||
          now > 8_640_000_000_000_000 - WORKFLOW_DESKTOP_DEADLINE_MS)
        throw new Error("Workflow clock is invalid.");
      const request: WorkflowInvokeParams = Object.freeze({
        workflowCallId: identity.invocationCallId,
        conversationId,
        runId: scope.runId,
        name: "workflow.invoke.v1",
        capabilityContext: expectedContext,
        idempotencyKey: identity.idempotencyKey,
        deadlineUtc: new Date(now + WORKFLOW_DESKTOP_DEADLINE_MS).toISOString(),
        input: boundedInput,
      });
      const result = await workflowBridge.invoke(request, context.signal, WORKFLOW_BRIDGE_TIMEOUT_MS);
      return validateWorkflowInvokeResult(result);
    },
  } satisfies AgentTool<unknown, WorkflowInvokeV1Result>;
}

export function createCapabilityRuntimeHooks(
  scope: CapabilityRunScope,
  ordinaryTools: readonly AgentTool[],
  capabilityTools: readonly AgentTool[],
): Pick<AgentRuntimeHooks, "beforeModel" | "beforeTool"> {
  const canonicalByModelName = canonicalToolMap(ordinaryTools);
  const capabilityToolNames = new Set(capabilityTools.map((tool) => tool.name));
  const modelNameByCanonical = new Map(
    [...canonicalByModelName.entries()].map(([modelName, canonicalName]) => [canonicalName, modelName]),
  );
  const allowedModelNames = (candidate: ValidatedCapabilityRuntimeCandidate): ReadonlySet<string> => {
    const names = new Set<string>();
    if (candidate.kind === "workflow") return names;
    for (const canonicalName of candidate.requiredTools) {
      const modelName = modelNameByCanonical.get(canonicalName);
      if (!modelName) return new Set<string>();
      names.add(modelName);
    }
    return names;
  };

  const allowedByCandidate = new Map(
    scope.snapshot.candidates
      .filter((candidate) => capabilityToolNames.has(candidate.modelToolName))
      .map((candidate) => [candidate.modelToolName, allowedModelNames(candidate)]),
  );
  return {
    beforeModel({ request }: AgentBeforeModelContext): AgentBeforeModelResult | undefined {
      const selected = scope.selected;
      if (!selected) return undefined;
      const allowed = allowedByCandidate.get(selected.modelToolName);
      return { tools: allowed ? request.tools.filter((tool) => allowed.has(tool.name)) : [] };
    },
    beforeTool({ tool }: AgentBeforeToolContext): AgentBeforeToolResult | undefined {
      const selected = scope.selected;
      if (!selected) return undefined;
      if (selected.kind === "workflow")
        return { skip: true, reason: "capability.workflow.direct_tool_denied" };
      if (capabilityToolNames.has(tool.name))
        return { skip: true, reason: "A Capability is already selected for this run." };
      const canonicalName = canonicalByModelName.get(tool.name);
      if (!canonicalName || !selected.requiredTools.includes(canonicalName))
        return { skip: true, reason: "The selected Capability does not allow this Tool." };
      return undefined;
    },
  };
}

export function prepareCapabilityTurn(
  scope: CapabilityRunScope,
  systemPrompt: string | undefined,
): { readonly systemPrompt: string } | undefined {
  const selected = scope.selected;
  if (!selected) return undefined;
  const projection = [
    "The Desktop approved this exact Capability for the current run only.",
    `Capability: ${selected.id}@${selected.version} (${selected.contentHash}).`,
    "Follow the selected Capability instructions below. Use only the Tools currently exposed by the runtime.",
    "<selected-capability-instructions>",
    selected.instructions,
    "</selected-capability-instructions>",
  ].join("\n");
  return { systemPrompt: systemPrompt ? `${systemPrompt}\n\n${projection}` : projection };
}

function validateCandidate(value: unknown): ValidatedCapabilityRuntimeCandidate {
  if (!isRecord(value) || !hasExactKeys(value, CANDIDATE_KEYS) ||
      !boundedString(value.id, 3, 128) || !CAPABILITY_ID_PATTERN.test(value.id) ||
      !boundedString(value.version, 5, 64) || !SEMANTIC_VERSION_PATTERN.test(value.version) ||
      typeof value.contentHash !== "string" || !HASH_PATTERN.test(value.contentHash) ||
      typeof value.catalogRevision !== "number" || !Number.isSafeInteger(value.catalogRevision) ||
      value.catalogRevision < 1 ||
      typeof value.modelToolName !== "string" || !MODEL_TOOL_PATTERN.test(value.modelToolName) ||
      !boundedString(value.displayName, 1, 80) || !boundedString(value.description, 1, 500) ||
      (value.kind !== "prompt" && value.kind !== "workflow") ||
      !boundedUniqueStrings(value.intentExamples, 1, MAXIMUM_INTENT_EXAMPLES, 160) ||
      !boundedString(value.instructions, 1, MAXIMUM_INSTRUCTIONS_CHARACTERS) ||
      !boundedString(value.canonicalInputSchemaJson, 2, MAXIMUM_SCHEMA_CHARACTERS) ||
      !boundedUniqueStrings(value.requiredTools, 0, MAXIMUM_REQUIRED_TOOLS, 128) ||
      typeof value.connectionBindingHash !== "string" || !HASH_PATTERN.test(value.connectionBindingHash) ||
      typeof value.maximumRisk !== "string" || !RISK_VALUES.has(value.maximumRisk))
    throw new Error("Capability runtime candidate is invalid.");

  const candidate = value as unknown as CapabilityRuntimeCandidate;
  const expectedModelToolName = computeCapabilityModelToolName(candidate);
  if (candidate.modelToolName !== expectedModelToolName)
    throw new Error("Capability model Tool name does not match its exact identity.");
  const inputSchema = parseInputSchema(candidate.canonicalInputSchemaJson);
  return Object.freeze({
    id: candidate.id,
    version: candidate.version,
    contentHash: candidate.contentHash,
    catalogRevision: candidate.catalogRevision,
    modelToolName: candidate.modelToolName,
    displayName: candidate.displayName,
    description: candidate.description,
    kind: candidate.kind,
    intentExamples: Object.freeze([...candidate.intentExamples]),
    instructions: candidate.instructions,
    canonicalInputSchemaJson: candidate.canonicalInputSchemaJson,
    requiredTools: Object.freeze([...candidate.requiredTools]),
    connectionBindingHash: candidate.connectionBindingHash,
    maximumRisk: candidate.maximumRisk,
    inputSchema,
  });
}

function createValidatedSnapshot(
  candidates: readonly ValidatedCapabilityRuntimeCandidate[],
): ValidatedCapabilityRuntimeSnapshot {
  return freezeSnapshot({
    schemaVersion: SNAPSHOT_SCHEMA_VERSION,
    snapshotHash: computeCapabilitySnapshotHash(candidates),
    candidates,
  });
}

function freezeSnapshot(snapshot: ValidatedCapabilityRuntimeSnapshot): ValidatedCapabilityRuntimeSnapshot {
  return Object.freeze({
    schemaVersion: snapshot.schemaVersion,
    snapshotHash: snapshot.snapshotHash,
    candidates: Object.freeze([...snapshot.candidates]),
  });
}

function createInvocationContext(
  snapshotHash: string,
  candidate: ValidatedCapabilityRuntimeCandidate,
): CapabilityInvocationContext {
  return Object.freeze({
    snapshotHash,
    modelToolName: candidate.modelToolName,
    capabilityId: candidate.id,
    version: candidate.version,
    contentHash: candidate.contentHash,
    catalogRevision: candidate.catalogRevision,
    connectionBindingHash: candidate.connectionBindingHash,
  });
}

function validateSelectionAcknowledgement(
  value: Readonly<Record<string, unknown>>,
  expected: CapabilityInvocationContext,
): CapabilitySelectV1Result {
  if (!isRecord(value) || !hasExactKeys(value, new Set(["selected", "context"])) || value.selected !== true ||
      !isRecord(value.context) || !hasExactKeys(value.context, CONTEXT_KEYS))
    throw new Error("Desktop returned an invalid Capability selection acknowledgement.");
  for (const key of CONTEXT_KEYS) {
    if (value.context[key] !== expected[key as keyof CapabilityInvocationContext])
      throw new Error("Desktop Capability selection acknowledgement does not match the frozen snapshot.");
  }
  return Object.freeze({ selected: true, context: expected });
}

export interface WorkflowInvocationIdentity {
  readonly selectionCallId: string;
  readonly invocationCallId: string;
  readonly idempotencyKey: string;
}

export function deriveWorkflowInvocationIdentity(
  modelToolCallId: string | undefined,
  conversationId: string,
  runId: string,
): WorkflowInvocationIdentity {
  if (!boundedString(modelToolCallId, 1, MAXIMUM_MODEL_TOOL_CALL_ID_BYTES) ||
      Buffer.byteLength(modelToolCallId, "utf8") > MAXIMUM_MODEL_TOOL_CALL_ID_BYTES ||
      !boundedString(conversationId, 1, 128) || !boundedString(runId, 1, 128) ||
      hasUnpairedSurrogate(modelToolCallId) || hasUnpairedSurrogate(conversationId) ||
      hasUnpairedSurrogate(runId))
    throw new Error("Workflow invocation requires a bounded model Tool call identity.");

  const callDigest = (format: string): string => {
    const hash = createHash("sha256");
    append(hash, "format", format);
    append(hash, "conversationId", conversationId);
    append(hash, "runId", runId);
    append(hash, "modelToolCallId", modelToolCallId);
    return hash.digest("hex");
  };
  const idempotencyHash = createHash("sha256");
  append(idempotencyHash, "format", "workflow-idempotency-v1");
  append(idempotencyHash, "modelToolCallId", modelToolCallId);
  return Object.freeze({
    selectionCallId: `wfsel_${callDigest("workflow-selection-call-v1").slice(0, 48)}`,
    invocationCallId: `wfinv_${callDigest("workflow-invocation-call-v1").slice(0, 48)}`,
    idempotencyKey: `wfid_${idempotencyHash.digest("hex")}`,
  });
}

function cloneBoundedWorkflowInput(value: unknown): Readonly<Record<string, unknown>> {
  return cloneBoundedJsonRecord(value, MAXIMUM_WORKFLOW_INPUT_BYTES, "Workflow input is invalid or too large.");
}

function validateWorkflowInvokeResult(value: unknown): WorkflowInvokeV1Result {
  const result = cloneBoundedJsonRecord(
    value,
    MAXIMUM_WORKFLOW_RESULT_ENVELOPE_BYTES,
    "Desktop returned an invalid Workflow result.",
    MAXIMUM_WORKFLOW_RESULT_ENVELOPE_DEPTH,
    MAXIMUM_WORKFLOW_RESULT_ENVELOPE_NODES,
  );
  if (result.result !== undefined) {
    cloneBoundedJsonRecord(
      result.result,
      MAXIMUM_WORKFLOW_FINAL_OUTPUT_BYTES,
      "Desktop returned an invalid Workflow result.",
    );
  }
  const keys = Object.keys(result);
  if (!keys.every((key) => WORKFLOW_RESULT_KEYS.has(key)) ||
      !Object.hasOwn(result, "workflowRunId") || !Object.hasOwn(result, "status") ||
      !Object.hasOwn(result, "nextAction") ||
      !boundedString(result.workflowRunId, 1, 128) ||
      typeof result.status !== "string" || !WORKFLOW_STATUSES.has(result.status) ||
      typeof result.nextAction !== "string" || !WORKFLOW_NEXT_ACTIONS.has(result.nextAction) ||
      result.nextAction !== WORKFLOW_NEXT_ACTION_BY_STATUS[
        result.status as keyof typeof WORKFLOW_NEXT_ACTION_BY_STATUS
      ] ||
      (result.summary !== undefined && !boundedString(result.summary, 1, 4_000)) ||
      (result.errorCode !== undefined &&
        (!boundedString(result.errorCode, 3, 128) || !WORKFLOW_ERROR_CODE_PATTERN.test(result.errorCode))) ||
      (result.result !== undefined && !isRecord(result.result)) ||
      !validRequiredInputs(result.requiredInputs))
    throw new Error("Desktop returned an invalid Workflow result.");
  if (result.status === "needs_information" &&
      (!Array.isArray(result.requiredInputs) || result.requiredInputs.length === 0 ||
       result.nextAction !== "provide_information"))
    throw new Error("Desktop returned an invalid Workflow result.");
  return result as unknown as WorkflowInvokeV1Result;
}

function validRequiredInputs(value: unknown): boolean {
  if (value === undefined) return true;
  if (!Array.isArray(value) || value.length < 1 || value.length > 32 ||
      new Set(value).size !== value.length) return false;
  return value.every((pointer) => typeof pointer === "string" && pointer.length >= 1 && pointer.length <= 512 &&
    /^(?:\/(?:[^~/]|~[01])*)+$/.test(pointer));
}

function cloneBoundedJsonRecord(
  value: unknown,
  maximumBytes: number,
  errorMessage: string,
  maximumDepth = MAXIMUM_RUNTIME_VALUE_DEPTH,
  maximumNodes = MAXIMUM_RUNTIME_VALUE_NODES,
): Readonly<Record<string, unknown>> {
  try {
    const counter = { nodes: 0 };
    const clone = cloneJsonValue(value, 0, counter, maximumDepth, maximumNodes);
    if (!isRecord(clone)) throw new Error(errorMessage);
    const serialized = JSON.stringify(clone);
    if (Buffer.byteLength(serialized, "utf8") > maximumBytes) throw new Error(errorMessage);
    return deepFreeze(clone);
  } catch (error) {
    if (error instanceof Error && error.message === errorMessage) throw error;
    throw new Error(errorMessage);
  }
}

function cloneJsonValue(
  value: unknown,
  depth: number,
  counter: { nodes: number },
  maximumDepth: number,
  maximumNodes: number,
): unknown {
  if (depth > maximumDepth || ++counter.nodes > maximumNodes)
    throw new Error("Runtime JSON value exceeds its structural bounds.");
  if (value === null || typeof value === "boolean") return value;
  if (typeof value === "string") {
    if (hasUnpairedSurrogate(value)) throw new Error("Runtime JSON string is invalid.");
    return value;
  }
  if (typeof value === "number") {
    if (!Number.isFinite(value) || (Number.isInteger(value) && !Number.isSafeInteger(value)))
      throw new Error("Runtime JSON number is invalid.");
    return Object.is(value, -0) ? 0 : value;
  }
  if (Array.isArray(value)) {
    if (value.length > MAXIMUM_RUNTIME_COLLECTION_ITEMS) throw new Error("Runtime JSON array is too large.");
    return value.map((item) => cloneJsonValue(item, depth + 1, counter, maximumDepth, maximumNodes));
  }
  if (!isPlainRecord(value)) throw new Error("Runtime JSON object is invalid.");
  const ownKeys = Reflect.ownKeys(value);
  const keys = Object.keys(value);
  if (ownKeys.length !== keys.length || keys.length > MAXIMUM_RUNTIME_COLLECTION_ITEMS)
    throw new Error("Runtime JSON object is too large.");
  const result: Record<string, unknown> = Object.create(null);
  for (const key of keys) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    if (!descriptor || !("value" in descriptor) ||
        key.length < 1 || key.length > MAXIMUM_RUNTIME_PROPERTY_NAME_CHARACTERS || hasUnpairedSurrogate(key))
      throw new Error("Runtime JSON property is invalid.");
    result[key] = cloneJsonValue(descriptor.value, depth + 1, counter, maximumDepth, maximumNodes);
  }
  return result;
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  if (!isRecord(value)) return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

function hasUnpairedSurrogate(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return true;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return true;
    }
  }
  return false;
}

function canonicalToolMap(tools: readonly AgentTool[]): ReadonlyMap<string, string> {
  const registrations = new Map(
    Object.entries(BUILT_IN_TOOL_REGISTRATIONS).map(([modelName, registration]) => [modelName, registration.desktopName]),
  );
  const result = new Map<string, string>();
  for (const tool of tools) {
    const canonical = registrations.get(tool.name) ?? (tool.name === "mcp_read" ? "mcp.read.v1" : undefined);
    if (canonical) result.set(tool.name, canonical);
  }
  return result;
}

function capabilityToolDescription(candidate: ValidatedCapabilityRuntimeCandidate): string {
  return [
    `Select the approved Capability '${candidate.displayName}' for this run.`,
    candidate.description,
    `Use for intents such as: ${candidate.intentExamples.join("; ")}.`,
    candidate.kind === "workflow"
      ? "The Desktop executes the approved Workflow; no ordinary Tools are exposed after selection."
      : "Selection does not execute a Windows effect; subsequent Tools still require Desktop policy checks.",
  ].join(" ");
}

function parseInputSchema(value: string): Readonly<Record<string, unknown>> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error("Capability input schema is not valid JSON.");
  }
  if (!isRecord(parsed)) throw new Error("Capability input schema root must be an object.");
  validateJsonValueBounds(parsed, 0);
  const counter = { properties: 0 };
  validateSchemaNode(parsed, 0, true, counter);
  return deepFreeze(parsed);
}

function validateJsonValueBounds(value: unknown, depth: number): void {
  if (depth > 32) throw new Error("Capability input schema JSON exceeds the depth limit.");
  if (Array.isArray(value)) {
    if (value.length > 128) throw new Error("Capability input schema JSON array is too large.");
    for (const item of value) validateJsonValueBounds(item, depth + 1);
    return;
  }
  if (isRecord(value)) {
    const entries = Object.entries(value);
    if (entries.length > 128) throw new Error("Capability input schema JSON object is too large.");
    for (const [, child] of entries) validateJsonValueBounds(child, depth + 1);
    return;
  }
  if (typeof value === "number" && !Number.isFinite(value))
    throw new Error("Capability input schema JSON contains a non-finite number.");
}

function validateSchemaNode(
  schema: Readonly<Record<string, unknown>>,
  depth: number,
  isRoot: boolean,
  counter: { properties: number },
): void {
  if (depth > MAXIMUM_SCHEMA_DEPTH || Object.keys(schema).some((key) => !SCHEMA_KEYWORDS.has(key)))
    throw new Error("Capability input schema contains an unsupported node.");
  const type = schema.type;
  if (typeof type !== "string" || !SCHEMA_TYPES.has(type) || (isRoot && type !== "object"))
    throw new Error("Capability input schema type is invalid.");
  if ("$schema" in schema && (!isRoot || schema.$schema !== "https://json-schema.org/draft/2020-12/schema"))
    throw new Error("Capability input schema declaration is invalid.");
  if ("title" in schema && !boundedString(schema.title, 1, 200))
    throw new Error("Capability input schema title is invalid.");
  if ("description" in schema &&
      (typeof schema.description !== "string" || schema.description.length > 1_000))
    throw new Error("Capability input schema description is invalid.");
  validateEnumAndConst(schema, type);

  if (type === "object") {
    const propertyMap = schema.properties;
    if (!isRecord(propertyMap) || schema.additionalProperties !== false)
      throw new Error("Capability input schema object is not bounded.");
    const properties = Object.entries(propertyMap);
    counter.properties += properties.length;
    if (counter.properties > MAXIMUM_SCHEMA_PROPERTIES ||
        properties.some(([name]) => name.length < 1 || name.length > 80))
      throw new Error("Capability input schema exceeds the property limit.");
    for (const [, child] of properties) {
      if (!isRecord(child)) throw new Error("Capability input schema property is invalid.");
      validateSchemaNode(child, depth + 1, false, counter);
    }
    if ("required" in schema) {
      if (!Array.isArray(schema.required) || schema.required.length > properties.length ||
          schema.required.some((name) => typeof name !== "string" || !(name in propertyMap)) ||
          new Set(schema.required).size !== schema.required.length)
        throw new Error("Capability input schema required list is invalid.");
    }
    rejectSchemaFields(schema, [
      "items", "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "minimum", "maximum",
      "exclusiveMinimum", "exclusiveMaximum", "format",
    ]);
    return;
  }

  if (type === "array") {
    if (!isRecord(schema.items) || !boundedInteger(schema.maxItems, 0, 64) ||
        ("minItems" in schema && !boundedInteger(schema.minItems, 0, schema.maxItems)) ||
        ("uniqueItems" in schema &&
          (typeof schema.uniqueItems !== "boolean" ||
            (schema.uniqueItems && schema.items.type !== "string"))))
      throw new Error("Capability input schema array is not bounded.");
    validateSchemaNode(schema.items, depth + 1, false, counter);
    rejectSchemaFields(schema, [
      "properties", "required", "additionalProperties", "minLength", "maxLength", "minimum", "maximum",
      "exclusiveMinimum", "exclusiveMaximum", "format",
    ]);
    return;
  }

  if (type === "string") {
    if (!boundedInteger(schema.maxLength, 0, 4_096) ||
        ("minLength" in schema && !boundedInteger(schema.minLength, 0, schema.maxLength)) ||
        ("format" in schema && (typeof schema.format !== "string" || !SCHEMA_FORMATS.has(schema.format))))
      throw new Error("Capability input schema string is not bounded.");
    rejectSchemaFields(schema, [
      "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "minimum", "maximum",
      "uniqueItems", "exclusiveMinimum", "exclusiveMaximum",
    ]);
    return;
  }

  if (type === "integer" || type === "number") {
    for (const name of ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"] as const) {
      if (name in schema && (typeof schema[name] !== "number" || !Number.isFinite(schema[name])))
        throw new Error("Capability input schema numeric bound is invalid.");
    }
    if (typeof schema.minimum === "number" && typeof schema.maximum === "number" &&
        schema.minimum > schema.maximum)
      throw new Error("Capability input schema numeric bounds are invalid.");
    rejectSchemaFields(schema, [
      "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "minLength",
      "maxLength", "uniqueItems", "format",
    ]);
    return;
  }

  rejectSchemaFields(schema, [
    "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "minLength", "maxLength",
    "uniqueItems", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "format",
  ]);
}

function validateEnumAndConst(schema: Readonly<Record<string, unknown>>, type: string): void {
  if ("enum" in schema) {
    if (!Array.isArray(schema.enum) || schema.enum.length < 1 || schema.enum.length > 64 ||
        schema.enum.some((value) => !matchesSchemaType(value, type)) ||
        new Set(schema.enum.map((value) => JSON.stringify(value))).size !== schema.enum.length)
      throw new Error("Capability input schema enum is invalid.");
  }
  if ("const" in schema && !matchesSchemaType(schema.const, type))
    throw new Error("Capability input schema const is invalid.");
}

function matchesSchemaType(value: unknown, type: string): boolean {
  switch (type) {
    case "string": return typeof value === "string";
    case "integer": return typeof value === "number" && Number.isSafeInteger(value);
    case "number": return typeof value === "number" && Number.isFinite(value);
    case "boolean": return typeof value === "boolean";
    case "array": return Array.isArray(value);
    case "object": return isRecord(value);
    default: return false;
  }
}

function rejectSchemaFields(schema: Readonly<Record<string, unknown>>, forbidden: readonly string[]): void {
  if (forbidden.some((name) => name in schema))
    throw new Error("Capability input schema keyword is not applicable to its type.");
}

function boundedInteger(value: unknown, minimum: number, maximum: number): value is number {
  return Number.isSafeInteger(value) && (value as number) >= minimum && (value as number) <= maximum;
}

function deepFreeze<T>(value: T): T {
  if (value && typeof value === "object") {
    for (const child of Object.values(value)) deepFreeze(child);
    Object.freeze(value);
  }
  return value;
}

function appendList(hash: Hash, name: string, values: readonly string[]): void {
  append(hash, `${name}.count`, String(values.length));
  for (const [index, value] of values.entries()) append(hash, `${name}[${index}]`, value);
}

function append(hash: Hash, name: string, value: string): void {
  const nameBytes = Buffer.from(name, "utf8");
  const valueBytes = Buffer.from(value, "utf8");
  const framing = Buffer.allocUnsafe(12);
  framing.writeInt32BE(nameBytes.length, 0);
  framing.writeBigInt64BE(BigInt(valueBytes.length), 4);
  hash.update(framing);
  hash.update(nameBytes);
  hash.update(valueBytes);
}

function boundedString(value: unknown, minimum: number, maximum: number): value is string {
  return typeof value === "string" && value.length >= minimum && value.length <= maximum;
}

function boundedUniqueStrings(
  value: unknown,
  minimumItems: number,
  maximumItems: number,
  maximumCharacters: number,
): value is readonly string[] {
  return Array.isArray(value) && value.length >= minimumItems && value.length <= maximumItems &&
    value.every((item) => boundedString(item, 1, maximumCharacters)) &&
    new Set(value).size === value.length;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Readonly<Record<string, unknown>>, expected: ReadonlySet<string>): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.size && keys.every((key) => expected.has(key));
}

function serializedSnapshotExceedsBudget(value: unknown): boolean {
  try {
    const serialized = JSON.stringify(value);
    return serialized === undefined || Buffer.byteLength(serialized, "utf8") > MAXIMUM_SERIALIZED_SNAPSHOT_BYTES;
  } catch {
    return true;
  }
}
