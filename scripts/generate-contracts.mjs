import { createHash } from "node:crypto";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const contractsRoot = path.join(repositoryRoot, "contracts");
const toolContractsRoot = path.join(contractsRoot, "tools");
const csharpOutput = path.join(repositoryRoot, "src", "LIGClaw.Contracts", "Generated", "Contracts.g.cs");
const typescriptOutput = path.join(repositoryRoot, "sidecar", "src", "generated", "contracts.ts");
const checkOnly = process.argv.includes("--check");
const canonicalToolNamePattern = /^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+\.v[1-9][0-9]*$/;

async function collectJsonFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...await collectJsonFiles(fullPath));
    else if (entry.name.endsWith(".json")) files.push(fullPath);
  }
  return files;
}

function pascalCase(value) {
  return value
    .replace(/[^A-Za-z0-9]+(.)/g, (_, character) => character.toUpperCase())
    .replace(/^[a-z]/, (character) => character.toUpperCase());
}

function createSchemaRegistry(schemas) {
  const byPath = new Map();
  const byTypeName = new Map();
  for (const schema of schemas) {
    if (typeof schema.value.title !== "string" || schema.value.title.length === 0) {
      throw new Error(`Contract schema must define a title: ${path.relative(contractsRoot, schema.filePath)}`);
    }
    const typeName = pascalCase(schema.value.title);
    const existing = byTypeName.get(typeName);
    if (existing) {
      throw new Error(
        `Contract schema titles must generate unique type names: ${path.relative(contractsRoot, existing.filePath)} and ${path.relative(contractsRoot, schema.filePath)}`,
      );
    }
    byPath.set(path.normalize(schema.filePath), schema);
    byTypeName.set(typeName, schema);
  }

  return {
    typeNameForReference(reference, ownerPath) {
      if (typeof reference !== "string" || reference.length === 0) {
        throw new Error(`Invalid local $ref in ${path.relative(contractsRoot, ownerPath)}.`);
      }
      const hashIndex = reference.indexOf("#");
      const fileReference = hashIndex === -1 ? reference : reference.slice(0, hashIndex);
      const fragment = hashIndex === -1 ? "" : reference.slice(hashIndex + 1);
      if (fragment.length > 0) {
        throw new Error(
          `JSON Pointer $ref fragments are not supported by the contract generator: ${reference}`,
        );
      }
      if (fileReference.length === 0) {
        const owner = byPath.get(path.normalize(ownerPath));
        if (!owner) throw new Error(`Unable to resolve local $ref: ${reference}`);
        return pascalCase(owner.value.title);
      }
      if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(fileReference) || fileReference.startsWith("//")) {
        throw new Error(`Only repository-local $ref values are supported: ${reference}`);
      }

      const targetPath = path.resolve(path.dirname(ownerPath), fileReference);
      const relativeTarget = path.relative(contractsRoot, targetPath);
      if (relativeTarget === ".." || relativeTarget.startsWith(`..${path.sep}`) || path.isAbsolute(relativeTarget)) {
        throw new Error(`Contract $ref escapes contracts/: ${reference}`);
      }
      const target = byPath.get(path.normalize(targetPath));
      if (!target) {
        throw new Error(
          `Unable to resolve contract $ref from ${path.relative(contractsRoot, ownerPath)}: ${reference}`,
        );
      }
      return pascalCase(target.value.title);
    },
  };
}

function ordinalCompare(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value !== null && typeof value === "object") {
    return `{${Object.keys(value)
      .sort(ordinalCompare)
      .map((name) => `${JSON.stringify(name)}:${canonicalJson(value[name])}`)
      .join(",")}}`;
  }
  return JSON.stringify(value);
}

export function createToolSchemaPairs(schemas, toolsRoot = toolContractsRoot) {
  const pairs = new Map();
  for (const schema of schemas) {
    const relativePath = path.relative(toolsRoot, schema.filePath);
    if (relativePath === ".." || relativePath.startsWith(`..${path.sep}`) || path.isAbsolute(relativePath)) {
      continue;
    }
    if (path.dirname(relativePath) !== ".") {
      throw new Error(`Tool contract schemas must be direct children of contracts/tools: ${relativePath}`);
    }

    const fileName = path.basename(relativePath);
    const isResult = fileName.endsWith(".result.schema.json");
    const suffix = isResult ? ".result.schema.json" : ".schema.json";
    if (!fileName.endsWith(suffix)) {
      throw new Error(`Unsupported Tool contract schema file name: ${fileName}`);
    }
    const canonicalName = fileName.slice(0, -suffix.length);
    if (!canonicalToolNamePattern.test(canonicalName)) {
      throw new Error(`Tool contract schema file name is not canonical: ${fileName}`);
    }

    const pair = pairs.get(canonicalName) ?? { canonicalName, input: undefined, result: undefined };
    const role = isResult ? "result" : "input";
    if (pair[role] !== undefined) {
      throw new Error(`Duplicate Tool ${role} schema for ${canonicalName}.`);
    }
    pair[role] = schema;
    pairs.set(canonicalName, pair);
  }

  const result = [];
  for (const pair of [...pairs.values()].sort((left, right) => ordinalCompare(left.canonicalName, right.canonicalName))) {
    if (pair.input === undefined) {
      throw new Error(`Tool contract is missing input schema: ${pair.canonicalName}.schema.json`);
    }
    if (pair.result === undefined) {
      throw new Error(`Tool contract is missing result schema: ${pair.canonicalName}.result.schema.json`);
    }
    result.push(pair);
  }
  return result;
}

function typescriptType(property, ownerPath, registry) {
  if (property.$ref !== undefined) return registry.typeNameForReference(property.$ref, ownerPath);
  if (property.const !== undefined) return JSON.stringify(property.const);
  if (property.enum) return property.enum.map((value) => JSON.stringify(value)).join(" | ");
  if (property.type === "string") return "string";
  if (property.type === "integer" || property.type === "number") return "number";
  if (property.type === "boolean") return "boolean";
  if (property.type === "array") return `readonly ${typescriptType(property.items, ownerPath, registry)}[]`;
  if (property.type === "object") return "Readonly<Record<string, unknown>>";
  throw new Error(`Unsupported TypeScript schema property: ${JSON.stringify(property)}`);
}

function csharpType(property, required, ownerPath, registry) {
  let type;
  if (property.$ref !== undefined) type = registry.typeNameForReference(property.$ref, ownerPath);
  else if (property.type === "string" && property.format === "date-time") type = "DateTimeOffset";
  else if (property.type === "string") type = "string";
  else if (property.type === "integer") type = "long";
  else if (property.type === "number") type = "double";
  else if (property.type === "boolean") type = "bool";
  else if (property.type === "array") type = `IReadOnlyList<${csharpType(property.items, true, ownerPath, registry)}>`;
  else if (property.type === "object") type = "IReadOnlyDictionary<string, object?>";
  else throw new Error(`Unsupported C# schema property: ${JSON.stringify(property)}`);
  return !required && (property.$ref !== undefined || type === "string" || type.startsWith("IReadOnly")) ? `${type}?` : type;
}

function canDefaultToNull(property) {
  return property.$ref !== undefined ||
    property.type === "string" ||
    property.type === "array" ||
    property.type === "object";
}

function renderTypescript(schemas, toolSchemaPairs, protocolVersion, contractHash, registry) {
  const lines = [
    "// <auto-generated />",
    "// Generated by scripts/generate-contracts.mjs. Do not edit directly.",
    "",
    `export const PROTOCOL_VERSION = ${JSON.stringify(protocolVersion)} as const;`,
    `export const CONTRACT_HASH = ${JSON.stringify(contractHash)} as const;`,
    "",
    "export interface ToolContractSchemas {",
    "  readonly canonicalName: string;",
    "  readonly canonicalInputSchemaJson: string;",
    "  readonly canonicalResultSchemaJson: string;",
    "}",
    "",
    "const mutableToolSchemaRegistry: Record<string, ToolContractSchemas> = Object.assign(Object.create(null), {",
  ];
  for (const pair of toolSchemaPairs) {
    lines.push(
      `  ${JSON.stringify(pair.canonicalName)}: Object.freeze({`,
      `    canonicalName: ${JSON.stringify(pair.canonicalName)},`,
      `    canonicalInputSchemaJson: ${JSON.stringify(canonicalJson(pair.input.value))},`,
      `    canonicalResultSchemaJson: ${JSON.stringify(canonicalJson(pair.result.value))},`,
      "  }),",
    );
  }
  lines.push(
    "});",
    "",
    "export const TOOL_SCHEMA_REGISTRY: Readonly<Record<string, ToolContractSchemas>> =",
    "  Object.freeze(mutableToolSchemaRegistry);",
    "",
    "export function tryGetToolContractSchemas(canonicalToolName: string): ToolContractSchemas | undefined {",
    "  return Object.hasOwn(TOOL_SCHEMA_REGISTRY, canonicalToolName)",
    "    ? TOOL_SCHEMA_REGISTRY[canonicalToolName]",
    "    : undefined;",
    "}",
    "",
  );
  for (const schema of schemas) {
    const required = new Set(schema.value.required ?? []);
    lines.push(`export interface ${pascalCase(schema.value.title)} {`);
    for (const [name, property] of Object.entries(schema.value.properties ?? {})) {
      lines.push(`  readonly ${name}${required.has(name) ? "" : "?"}: ${typescriptType(property, schema.filePath, registry)};`);
    }
    lines.push("}", "");
  }
  return `${lines.join("\r\n").trimEnd()}\r\n`;
}

function renderCsharp(schemas, toolSchemaPairs, protocolVersion, contractHash, registry) {
  const lines = [
    "// <auto-generated />",
    "// Generated by scripts/generate-contracts.mjs. Do not edit directly.",
    "#nullable enable",
    "using System.Collections.Frozen;",
    "using System.Text.Json.Serialization;",
    "",
    "namespace LIGClaw.Contracts.Generated;",
    "",
    "public static class ContractMetadata",
    "{",
    `    public const string ProtocolVersion = ${JSON.stringify(protocolVersion)};`,
    `    public const string Hash = ${JSON.stringify(contractHash)};`,
    "}",
    "",
    "public sealed record ToolContractSchemas(",
    "    string CanonicalName,",
    "    string CanonicalInputSchemaJson,",
    "    string CanonicalResultSchemaJson);",
    "",
    "public static class ToolSchemaRegistry",
    "{",
    "    public static FrozenDictionary<string, ToolContractSchemas> All { get; } =",
    "        new Dictionary<string, ToolContractSchemas>(StringComparer.Ordinal)",
    "        {",
  ];
  for (const pair of toolSchemaPairs) {
    lines.push(
      `            [${JSON.stringify(pair.canonicalName)}] = new(`,
      `                ${JSON.stringify(pair.canonicalName)},`,
      `                ${JSON.stringify(canonicalJson(pair.input.value))},`,
      `                ${JSON.stringify(canonicalJson(pair.result.value))}),`,
    );
  }
  lines.push(
    "        }.ToFrozenDictionary(StringComparer.Ordinal);",
    "",
    "    public static bool TryGet(string canonicalToolName, out ToolContractSchemas? schemas)",
    "    {",
    "        ArgumentNullException.ThrowIfNull(canonicalToolName);",
    "        return All.TryGetValue(canonicalToolName, out schemas);",
    "    }",
    "}",
    "",
  );
  for (const schema of schemas) {
    const typeName = pascalCase(schema.value.title);
    const properties = Object.entries(schema.value.properties ?? {});
    const required = new Set(schema.value.required ?? []);
    if (properties.length === 0) {
      lines.push(`public sealed record ${typeName}();`, "");
      continue;
    }
    let defaultableSuffixStart = properties.length;
    while (defaultableSuffixStart > 0) {
      const [name, property] = properties[defaultableSuffixStart - 1];
      if (required.has(name) || !canDefaultToNull(property)) break;
      defaultableSuffixStart--;
    }
    lines.push(`public sealed record ${typeName}(`);
    properties.forEach(([name, property], index) => {
      const suffix = index === properties.length - 1 ? ");" : ",";
      const isRequired = required.has(name);
      const defaultValue = index >= defaultableSuffixStart ? " = null" : "";
      const attributes = !isRequired && canDefaultToNull(property)
        ? `[property: JsonPropertyName(${JSON.stringify(name)}), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`
        : `[property: JsonPropertyName(${JSON.stringify(name)})]`;
      lines.push(`    ${attributes} ${csharpType(property, isRequired, schema.filePath, registry)} ${pascalCase(name)}${defaultValue}${suffix}`);
    });
    lines.push("");
  }
  return `${lines.join("\r\n").trimEnd()}\r\n`;
}

async function writeOrCheck(filePath, expected) {
  if (checkOnly) {
    const actual = await readFile(filePath, "utf8").catch(() => "");
    if (actual.replaceAll("\r\n", "\n") !== expected.replaceAll("\r\n", "\n")) {
      throw new Error(`Generated contract drift detected: ${path.relative(repositoryRoot, filePath)}`);
    }
    return;
  }
  await mkdir(path.dirname(filePath), { recursive: true });
  await writeFile(filePath, expected, "utf8");
}

async function main() {
  const files = await collectJsonFiles(contractsRoot);
  const parsed = await Promise.all(files.map(async (filePath) => ({
    filePath,
    value: JSON.parse(await readFile(filePath, "utf8")),
  })));
  const canonical = parsed
    .map(({ filePath, value }) => `${path.relative(contractsRoot, filePath).replaceAll("\\", "/")}\n${JSON.stringify(value)}`)
    .join("\n");
  const contractHash = createHash("sha256").update(canonical).digest("hex");
  const manifest = parsed.find(({ filePath }) => path.basename(filePath) === "protocol.json")?.value;
  if (!manifest?.protocolVersion) throw new Error("contracts/protocol.json must define protocolVersion.");
  const schemas = parsed
    .filter(({ filePath }) => filePath.endsWith(".schema.json"))
    .sort((left, right) => left.value.title.localeCompare(right.value.title));
  const registry = createSchemaRegistry(schemas);
  const toolSchemaPairs = createToolSchemaPairs(schemas);

  await writeOrCheck(
    typescriptOutput,
    renderTypescript(schemas, toolSchemaPairs, manifest.protocolVersion, contractHash, registry),
  );
  await writeOrCheck(
    csharpOutput,
    renderCsharp(schemas, toolSchemaPairs, manifest.protocolVersion, contractHash, registry),
  );
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
