import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFile, writeFile, unlink } from "node:fs/promises";
import { test } from "node:test";

// Exercise the actual embedded constructors, which npm audit cannot inspect.
const bundles = [
  ["llms", "index.js"], ["llms", "models.js"], ["llms", "providers.js"],
  ["agents", "index.js"], ["shared", "index.js"],
  ["shared", "automation/index.js"], ["shared", "remote-config/index.js"],
];
for (const [packageName, relative] of bundles) {
  test(`bundled ${packageName}/${relative} rejects oversized collections before element parsing`, async () => {
    const packageEntry = new URL(import.meta.resolve(`@cline/${packageName}`));
    const sourceUrl = new URL(relative, packageEntry);
    const source = await readFile(sourceUrl, "utf8");
    const names = {};
    for (const kind of ["Array", "Map", "Set", "String", "ObjectJIT", "CheckMaxLength", "CheckMaxSize", "CheckLengthEquals", "CheckSizeEquals", "CheckOverwrite"]) {
      const match = new RegExp(`([$\\w]+)=[$\\w]+\\("\\$Zod${kind}",`).exec(source);
      assert.ok(match, `Missing ${kind} constructor in ${sourceUrl}`);
      names[kind] = match[1];
    }
    const fixture = new URL(`.size-gate-${randomUUID()}.mjs`, sourceUrl);
    try {
      const exports = Object.entries(names).map(([key, value]) => `${key}:${value}`).join(",");
      await writeFile(fixture, `${source}\nexport const __regressionZod = {${exports}};\n`);
      const { __regressionZod: z } = await import(fixture.href);
      let parsed = 0;
      const element = new z.String({ type: "string" });
      const run = element._zod.run;
      element._zod.run = (...args) => { parsed++; return run(...args); };
      const scenarios = [
        [new z.Array({ type: "array", element, checks: [new z.CheckMaxLength({ check: "max_length", maximum: 1 })] }), [1, 2, 3]],
        [new z.Map({ type: "map", keyType: element, valueType: element, checks: [new z.CheckMaxSize({ check: "max_size", maximum: 1 })] }), new Map([[1, 1], [2, 2]])],
        [new z.Set({ type: "set", valueType: element, checks: [new z.CheckMaxSize({ check: "max_size", maximum: 1 })] }), new Set([1, 2, 3])],
        [new z.Array({ type: "array", element, checks: [new z.CheckLengthEquals({ check: "length_equals", length: 1 })] }), [1, 2, 3]],
        [new z.Map({ type: "map", keyType: element, valueType: element, checks: [new z.CheckSizeEquals({ check: "size_equals", size: 1 })] }), new Map([[1, 1], [2, 2]])],
        [new z.Set({ type: "set", valueType: element, checks: [new z.CheckSizeEquals({ check: "size_equals", size: 1 })] }), new Set([1, 2, 3])],
      ];
      for (const [schema, value] of scenarios) {
        for (const async of [false, true]) {
          parsed = 0;
          const result = await schema._zod.run({ value, issues: [] }, { async });
          assert.equal(parsed, 0);
          assert.equal(result.issues.length, 1);
          assert.equal(result.issues[0].code, "too_big");
        }
        parsed = 0;
        const object = new z.ObjectJIT({ type: "object", shape: { items: schema } });
        const nested = await object._zod.run({ value: { items: value }, issues: [] }, { async: false });
        assert.equal(parsed, 0);
        assert.equal(nested.issues.length, 1);
        assert.deepEqual(nested.issues[0].path, ["items"]);
        const invalid = await schema._zod.run({ value: false, issues: [] }, { async: false });
        assert.equal(invalid.issues[0].code, "invalid_type");
        for (const context of [{ skipChecks: true }, { direction: "backward" }]) {
          parsed = 0;
          await schema._zod.parse({ value, issues: [] }, context);
          assert.ok(parsed > 0, "Explicit skip/backward context must retain the original parser");
        }
      }
      // An earlier transformation can reduce the collection before the bound:
      // it must retain its original ordering instead of being preempted by a gate.
      const transformed = new z.Array({
        type: "array", element,
        checks: [
          new z.CheckOverwrite({ check: "overwrite", tx: (value) => value.slice(0, 1) }),
          new z.CheckMaxLength({ check: "max_length", maximum: 1 }),
        ],
      });
      const result = await transformed._zod.run({ value: ["a", "b"], issues: [] }, { async: false });
      assert.deepEqual(result.issues, []);
      assert.deepEqual(result.value, ["a"]);
    } finally {
      await unlink(fixture).catch(() => {});
    }
  });
}
