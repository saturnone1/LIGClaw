import type { ProviderConfigureParams, ProviderTestResult } from "../generated/contracts.js";

export const PROVIDER_TEST_TIMEOUT_MS = 120_000;

export async function testProviderConnection(
  configuration: ProviderConfigureParams,
  fetchImplementation: typeof fetch = fetch,
  timeoutMs = PROVIDER_TEST_TIMEOUT_MS,
): Promise<ProviderTestResult> {
  const endpoint = chatCompletionsEndpoint(configuration.baseUrl);

  try {
    const response = await fetchImplementation(endpoint, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${configuration.apiKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: configuration.model,
        messages: [{ role: "user", content: "Reply with OK." }],
        max_tokens: 8,
        stream: false,
      }),
      signal: AbortSignal.timeout(timeoutMs),
    });

    if (!response.ok) {
      await response.body?.cancel().catch(() => undefined);
      return { success: false, message: describeProviderFailure({ status: response.status }, timeoutMs) };
    }

    await response.body?.cancel().catch(() => undefined);
    return { success: true, message: "모델 연결을 확인했어요." };
  } catch (error) {
    return { success: false, message: describeProviderFailure(error, timeoutMs) };
  }
}

export function chatCompletionsEndpoint(baseUrl: string): string {
  const normalized = baseUrl.trim().replace(/\/+$/, "");
  return normalized.endsWith("/chat/completions") ? normalized : `${normalized}/chat/completions`;
}

export function describeProviderFailure(error: unknown, timeoutMs = PROVIDER_TEST_TIMEOUT_MS): string {
  const status = findHttpStatus(error);
  if (status === 401) return "API 키 인증에 실패했습니다 (HTTP 401). 새 API 키를 발급해 다시 입력해 주세요.";
  if (status === 403) return "API 접근이 거부됐습니다 (HTTP 403). 키의 권한과 해당 모델 사용 권한을 확인해 주세요.";
  if (status === 404) return "API 주소 또는 모델을 찾지 못했습니다 (HTTP 404). Base URL과 모델 이름을 확인해 주세요.";
  if (status === 408) return "모델 공급자의 요청 시간이 초과됐습니다 (HTTP 408). 잠시 후 다시 시도해 주세요.";
  if (status === 429) return "요청 한도에 도달했습니다 (HTTP 429). 공급자 사용량과 요금 한도를 확인해 주세요.";
  if (status === 400 || status === 422) {
    return `모델 공급자가 OpenAI 호환 요청을 거부했습니다 (HTTP ${status}). Base URL과 모델 이름을 확인해 주세요.`;
  }
  if (status !== undefined && status >= 500) {
    return `모델 공급자 서버 오류입니다 (HTTP ${status}). 잠시 후 다시 시도해 주세요.`;
  }
  if (status !== undefined) return `모델 공급자가 요청을 거부했습니다 (HTTP ${status}).`;

  const message = collectErrorText(error).toLowerCase();
  if (/timeout|timed out|aborterror|timeouterror|aborted/.test(message)) {
    return `연결 테스트가 ${Math.round(timeoutMs / 1000)}초 안에 완료되지 않았습니다. 네트워크 상태를 확인한 뒤 다시 시도해 주세요.`;
  }
  if (/enotfound|eai_again|econnrefused|econnreset|fetch failed|network|socket|certificate|self[- ]signed|tls/.test(message)) {
    return "모델 서버에 네트워크로 연결하지 못했습니다. 인터넷 연결, 프록시, 방화벽 또는 TLS 인증서를 확인해 주세요.";
  }
  return "모델 서버 호출 중 알 수 없는 오류가 발생했습니다. Base URL과 네트워크 설정을 확인해 주세요.";
}

function findHttpStatus(error: unknown): number | undefined {
  if (typeof error === "number" && error >= 400 && error <= 599) return error;
  if (typeof error === "string") {
    const match = error.match(/(?:http(?: status)?[^0-9]*)?\b([45][0-9]{2})\b/i);
    return match ? Number(match[1]) : undefined;
  }
  if (typeof error !== "object" || error === null) return undefined;
  const record = error as Record<string, unknown>;
  return findHttpStatus(record.status) ?? findHttpStatus(record.statusCode) ?? findHttpStatus(record.code);
}

function collectErrorText(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return `${error.name} ${error.message}`;
  if (typeof error === "object" && error !== null) {
    const record = error as Record<string, unknown>;
    return [record.message, record.code].map(collectErrorText).join(" ");
  }
  return "";
}
