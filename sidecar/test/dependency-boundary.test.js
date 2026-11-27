import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { test } from "node:test";
import { getProviderIds, hasProvider, getProvider } from "@cline/llms";
import * as browserCatalog from "@cline/llms/browser";

const sidecarRoot = new URL("../", import.meta.url);

test("the installed dependency graph has no Dify provider or Zod 3", async () => {
  const lock = JSON.parse(await readFile(new URL("package-lock.json", sidecarRoot), "utf8"));
  for (const [path, entry] of Object.entries(lock.packages)) {
    assert.doesNotMatch(path, /(?:^|\/)node_modules\/dify-ai-provider$/);
    assert.equal(Object.hasOwn(entry.dependencies ?? {}, "dify-ai-provider"), false, path);
    if (/(?:^|\/)node_modules\/zod$/.test(path)) {
      assert.match(entry.version, /^4\./, path);
    }
    if (/(?:^|\/)node_modules\/@cline\/(?:agents|llms|shared)$/.test(path)) {
      assert.equal(entry.version, "0.0.65-ligclaw.2", path);
    }
  }
  const require = createRequire(import.meta.url);
  assert.throws(() => require.resolve("dify-ai-provider"), { code: "MODULE_NOT_FOUND" });
});

test("Node and browser provider catalogs reject Dify while keeping compatible endpoints", async () => {
  for (const catalog of [{ getProviderIds, hasProvider, getProvider }, browserCatalog]) {
    assert.equal(catalog.hasProvider("dify"), false);
    assert.equal(catalog.getProviderIds().includes("dify"), false);
    assert.equal(await catalog.getProvider("dify"), undefined);
    assert.equal(catalog.hasProvider("openai-compatible"), true);
    assert.ok(await catalog.getProvider("openai-compatible"));
  }
});

test("the installed SDKs match the reviewed local archives and provenance", async () => {
  for (const name of ["agents", "llms", "shared"]) {
    const provenance = JSON.parse(await readFile(new URL(`vendor/cline-${name}-provenance.json`, sidecarRoot), "utf8"));
    const archive = await readFile(new URL(`vendor/${provenance.archive}`, sidecarRoot));
    assert.equal(createHash("sha256").update(archive).digest("hex"), provenance.sha256);
    const manifest = JSON.parse(await readFile(new URL("../package.json", import.meta.resolve(`@cline/${name}`)), "utf8"));
    assert.equal(manifest.version, provenance.version);
    assert.equal(Object.hasOwn(manifest.dependencies, "dify-ai-provider"), false);
  }
});
