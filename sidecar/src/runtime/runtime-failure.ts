export const RUNTIME_FAILURE_CODES = [
  "runtime.provider_authentication",
  "runtime.provider_permission",
  "runtime.provider_model_not_found",
  "runtime.provider_rate_limited",
  "runtime.provider_request_invalid",
  "runtime.provider_unavailable",
  "runtime.network_unavailable",
  "runtime.timeout",
  "runtime.configuration",
  "runtime.context_limit",
  "runtime.unknown",
] as const;

export type RuntimeFailureCode = typeof RUNTIME_FAILURE_CODES[number];

export function classifyRuntimeFailure(error: unknown): RuntimeFailureCode {
  if (typeof error === "string" && RUNTIME_FAILURE_CODES.includes(error as RuntimeFailureCode))
    return error as RuntimeFailureCode;
  const message = errorText(error).toLowerCase();
  const status = httpStatus(error, message);
  if (status === 401 || /unauthorized|invalid api key|authentication/.test(message)) return "runtime.provider_authentication";
  if (status === 403 || /forbidden|permission denied/.test(message)) return "runtime.provider_permission";
  if (status === 404 || /model.{0,30}(not found|does not exist|unknown)/.test(message)) return "runtime.provider_model_not_found";
  if (status === 429 || /rate.?limit|too many requests/.test(message)) return "runtime.provider_rate_limited";
  if (status === 400 || status === 422) return "runtime.provider_request_invalid";
  if (status !== undefined && status >= 500) return "runtime.provider_unavailable";
  if (/context.{0,30}(length|limit|window)|too many tokens|maximum context/.test(message)) return "runtime.context_limit";
  if (/unknown or disabled provider|provider.{0,20}(disabled|not registered)/.test(message)) return "runtime.configuration";
  if (/timeout|timed out|aborterror|timeouterror/.test(message)) return "runtime.timeout";
  if (/enotfound|eai_again|econnrefused|econnreset|fetch failed|network|socket|certificate|self[- ]signed|tls/.test(message))
    return "runtime.network_unavailable";
  return "runtime.unknown";
}

function httpStatus(error: unknown, message: string): number | undefined {
  if (typeof error === "object" && error !== null) {
    const record = error as Record<string, unknown>;
    for (const value of [record.status, record.statusCode, record.code]) {
      if (typeof value === "number" && value >= 400 && value <= 599) return value;
      if (typeof value === "string" && /^[45][0-9]{2}$/.test(value)) return Number(value);
    }
  }
  const match = message.match(/\b([45][0-9]{2})\b/);
  return match ? Number(match[1]) : undefined;
}

function errorText(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return `${error.name} ${error.message} ${errorText(error.cause)}`;
  if (typeof error === "object" && error !== null) {
    const record = error as Record<string, unknown>;
    return [record.message, record.code, record.cause].map(errorText).join(" ");
  }
  return "";
}
