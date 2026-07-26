import assert from "node:assert/strict";
import test from "node:test";
import {
  chatCompletionsEndpoint,
  describeProviderFailure,
  PROVIDER_TEST_TIMEOUT_MS,
  testProviderConnection,
} from "../dist/runtime/provider-connection-probe.js";

const configuration = {
  baseUrl: "https://models.example/v1",
  apiKey: "test-secret-key",
  model: "test-model",
};

test("connection probe calls the OpenAI-compatible chat completions endpoint", async () => {
  let capturedUrl;
  let capturedRequest;
  const result = await testProviderConnection(configuration, async (url, request) => {
    capturedUrl = url;
    capturedRequest = request;
    return new Response(JSON.stringify({ choices: [] }), { status: 200 });
  });

  assert.deepEqual(result, { success: true, message: "모델 연결을 확인했어요." });
  assert.equal(capturedUrl, "https://models.example/v1/chat/completions");
  assert.equal(capturedRequest.method, "POST");
  assert.equal(JSON.parse(capturedRequest.body).model, "test-model");
});

test("provider HTTP errors are classified without reading sensitive response details", async () => {
  const result = await testProviderConnection(configuration, async () =>
    new Response("api_key=test-secret-key prompt=private-input", { status: 401 }));

  assert.equal(result.success, false);
  assert.match(result.message, /HTTP 401/);
  assert.doesNotMatch(result.message, /test-secret-key|private-input/);
});

test("slow reasoning models receive a two minute connection-test window", () => {
  assert.equal(PROVIDER_TEST_TIMEOUT_MS, 120_000);
  assert.match(describeProviderFailure(new DOMException("timed out", "TimeoutError")), /120초/);
});

test("OpenAI-compatible request errors identify unsupported payloads safely", () => {
  const message = describeProviderFailure({ status: 422, message: "unsupported field with sensitive body" });
  assert.match(message, /HTTP 422/);
  assert.doesNotMatch(message, /sensitive body/);
});

test("an existing chat completions path is not duplicated", () => {
  assert.equal(
    chatCompletionsEndpoint("https://models.example/v1/chat/completions/"),
    "https://models.example/v1/chat/completions",
  );
});
