export const JSON_RPC_VERSION = "2.0" as const;
export { CONTRACT_HASH, PROTOCOL_VERSION } from "./generated/contracts.js";
export type {
  AgentEvent,
  ConversationCancelParams,
  ConversationCancelResult,
  ConversationStartParams,
  ConversationStartResult,
  InitializeParams,
  InitializeResult,
  McpCallParams,
  McpCallResult,
  McpConfigureParams,
  McpConfigureResult,
  McpStatusParams,
  McpStatusResult,
  PingResult,
  ProviderConfigureParams,
  ProviderConfigureResult,
  ProviderTestResult,
  ToolInvokeParams,
  ToolResultParams,
  ToolResultResult,
} from "./generated/contracts.js";
export const MAXIMUM_HEADER_BYTES = 8 * 1024;
export const MAXIMUM_PAYLOAD_BYTES = 4 * 1024 * 1024;

export interface RpcRequest {
  readonly jsonrpc: typeof JSON_RPC_VERSION;
  readonly id: string;
  readonly method: string;
  readonly params?: unknown;
}

export interface RpcResponse {
  readonly jsonrpc: typeof JSON_RPC_VERSION;
  readonly id: string;
  readonly result?: unknown;
  readonly error?: { readonly code: number; readonly message: string };
}

export function isRpcRequest(value: unknown): value is RpcRequest {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as Partial<RpcRequest>;
  return candidate.jsonrpc === JSON_RPC_VERSION &&
    typeof candidate.id === "string" &&
    typeof candidate.method === "string";
}
