// Apply the reviewed interpreter size gate at the same constructor boundary as upstream.
// Input/output are JSON over stdio, used only by the offline vendor recipe.
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
const require = createRequire(new URL("../sidecar/package.json", import.meta.url));
const ts = require("typescript");
const helper = (await readFile(new URL("bundled-zod-size-gate.js", import.meta.url), "utf8")).replaceAll("\r\n", "\n");
let input = "";
for await (const chunk of process.stdin) input += chunk;
const files = JSON.parse(input);
const output = {};
for (const [name, source] of Object.entries(files)) {
  if (!/major:4,minor:4,patch:3/.test(source) || source.includes("__ligclawCapCollection")) {
    throw new Error(`Unexpected bundled Zod baseline: ${name}`);
  }
  const ast = ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  if (ast.parseDiagnostics.length) throw new Error(`Cannot parse ${name}`);
  const insertions = [];
  const found = new Set();
  function visit(node) {
    if (ts.isCallExpression(node) && node.arguments.length && ts.isStringLiteral(node.arguments[0])) {
      const kind = /^\$Zod(Array|Map|Set)$/.exec(node.arguments[0].text)?.[1];
      if (kind) {
        const initialize = node.arguments[1];
        if (!ts.isArrowFunction(initialize) || !ts.isBlock(initialize.body) ||
            !ts.isIdentifier(initialize.parameters[0]?.name) || found.has(kind)) {
          throw new Error(`Unexpected ${kind} constructor in ${name}`);
        }
        found.add(kind);
        insertions.push({ at: initialize.body.end - 1, code: `;__ligclawCapCollection(${initialize.parameters[0].name.text},"${kind}");` });
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(ast);
  if (found.size !== 3) throw new Error(`Missing collection constructors in ${name}`);
  let patched = source;
  for (const insertion of insertions.sort((a, b) => b.at - a.at)) {
    patched = patched.slice(0, insertion.at) + insertion.code + patched.slice(insertion.at);
  }
  output[name] = `${patched}\n${helper}`;
}
process.stdout.write(JSON.stringify(output));
