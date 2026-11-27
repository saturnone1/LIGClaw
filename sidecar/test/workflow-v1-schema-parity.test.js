import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";

const schemaUrl = new URL("../../contracts/capabilities/workflow-v1.schema.json", import.meta.url);
const fixtureUrl = new URL("../../tests/fixtures/workflow-v1-schema-parity.json", import.meta.url);

const [schema, fixtures] = await Promise.all(
  [schemaUrl, fixtureUrl].map(async (url) => JSON.parse(await readFile(url, "utf8"))),
);
const validate = new Ajv2020({ allErrors: true, strict: true }).compile(schema);

test("workflow v1 contract matches the shared parser/static grammar fixtures", async (context) => {
  for (const fixture of fixtures) {
    await context.test(fixture.name, () => {
      const actual = validate(fixture.document);
      assert.equal(
        actual,
        fixture.valid,
        `${fixture.name}: ${JSON.stringify(validate.errors ?? [])}`,
      );
    });
  }
});
