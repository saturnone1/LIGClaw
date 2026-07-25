import assert from "node:assert/strict";
import { test } from "node:test";
import { frameMessage, MessageDecoder } from "../dist/framing.js";

test("decoder handles fragmented consecutive frames", async () => {
  const decoder = new MessageDecoder();
  const decoded = [];
  decoder.on("data", (value) => decoded.push(value));

  const bytes = Buffer.concat([frameMessage({ id: 1 }), frameMessage({ id: 2 })]);
  decoder.write(bytes.subarray(0, 7));
  decoder.write(bytes.subarray(7, 31));
  decoder.end(bytes.subarray(31));
  await new Promise((resolve, reject) => {
    decoder.on("finish", resolve);
    decoder.on("error", reject);
  });

  assert.deepEqual(decoded, [{ id: 1 }, { id: 2 }]);
});

test("decoder rejects a truncated frame", async () => {
  const decoder = new MessageDecoder();
  const framed = frameMessage({ id: 1 });

  const error = new Promise((resolve) => decoder.once("error", resolve));
  decoder.end(framed.subarray(0, framed.length - 1));

  await assert.rejects(
    async () => { throw await error; },
    /incomplete message/,
  );
});
