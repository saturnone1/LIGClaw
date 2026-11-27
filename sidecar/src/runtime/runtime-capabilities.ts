const WORKFLOW_RUNTIME_CAPABILITIES = Object.freeze([
  "workflow.invoke",
  "workflow.result",
  "workflow.runtime.v1",
] as const);

export function workflowRuntimeCapabilities(
  workflowBridgeWired: boolean,
): readonly (typeof WORKFLOW_RUNTIME_CAPABILITIES)[number][] {
  return workflowBridgeWired ? WORKFLOW_RUNTIME_CAPABILITIES : [];
}
