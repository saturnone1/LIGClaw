export const JSON_RPC_VERSION = "2.0" as const;
export const PROTOCOL_VERSION = "1.0" as const;
export const CONTRACT_HASH = "phase0-v1" as const;
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

export interface InitializeParams {
  readonly protocolVersion: string;
  readonly hostVersion: string;
  readonly contractHash: string;
  readonly sessionToken: string;
}

export interface InitializeResult {
  readonly protocolVersion: string;
  readonly sidecarVersion: string;
  readonly contractHash: string;
  readonly capabilities: readonly string[];
}

export function isRpcRequest(value: unknown): value is RpcRequest {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as Partial<RpcRequest>;
  return candidate.jsonrpc === JSON_RPC_VERSION &&
    typeof candidate.id === "string" &&
    typeof candidate.method === "string";
}
