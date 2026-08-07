export interface MemoryLookupIntent {
  readonly query: string;
  readonly label: string;
}

export interface MemoryRememberIntent {
  readonly key: string;
  readonly label: string;
  readonly value: string;
}

interface MemoryCategory {
  readonly key: string;
  readonly label: string;
  readonly aliases: readonly string[];
}

const MEMORY_CATEGORIES: readonly MemoryCategory[] = [
  { key: "editor", label: "선호 에디터", aliases: ["에디터", "편집기", "editor", "IDE"] },
  { key: "browser", label: "선호 브라우저", aliases: ["브라우저", "browser"] },
  { key: "terminal", label: "선호 터미널", aliases: ["터미널", "terminal"] },
  { key: "language", label: "선호 언어", aliases: ["프로그래밍 언어", "개발 언어", "language"] },
  { key: "theme", label: "선호 테마", aliases: ["테마", "theme"] },
];

const MEMORY_SUBJECT_PATTERN = MEMORY_CATEGORIES
  .flatMap(category => category.aliases)
  .sort((left, right) => right.length - left.length)
  .map(alias => escapeRegularExpression(alias).replace(/\\ /gu, "\\s*"))
  .join("|");

const REMEMBER_PATTERN = new RegExp(
  `(?:내가\\s*)?(?:선호(?:하는)?\\s*)?(${MEMORY_SUBJECT_PATTERN})(?:는|은|가|이)\\s*(.+?)\\s*(?:라고\\s*)?(?:기억해\\s*줘|저장해\\s*줘|기억해|저장해)\\s*[.!?]?$`,
  "iu",
);

export function detectMemoryLookupIntent(input: string): MemoryLookupIntent | undefined {
  const text = input.trim();
  if (!text || /(?:시스템\s*메모리|메모리\s*(?:사용|용량|상태)|RAM)/iu.test(text)) return undefined;
  const asksForPersonalValue = /(?:내|나의|내가|선호|좋아하|기억해\s*둔|저장해\s*둔)/u.test(text) &&
    /(?:뭐|무엇|어떤|알려|기억나|기억하고\s*있|저장돼|저장되어)/u.test(text);
  if (!asksForPersonalValue) return undefined;
  for (const category of MEMORY_CATEGORIES) {
    if (matchesCategory(text, category)) return { query: category.key, label: category.label };
  }
  return undefined;
}

export function formatMemoryLookupResult(
  label: string,
  output: Readonly<Record<string, unknown>>,
): string {
  const memories = Array.isArray(output.memories)
    ? output.memories.filter(isMemoryItem)
    : [];
  if (memories.length === 0) return `저장된 기억에서 ${escapeMarkdown(label)} 항목을 찾지 못했습니다.`;
  if (memories.length === 1)
    return `저장된 ${escapeMarkdown(label)}는 **${escapeMarkdown(memories[0]!.value)}**입니다.`;
  const rows = memories.slice(0, 10).map(item => `- ${escapeMarkdown(item.key)}: ${escapeMarkdown(item.value)}`);
  return [`저장된 기억에서 관련 항목 ${rows.length}개를 찾았습니다.`, "", ...rows].join("\n");
}

export function detectMemoryRememberIntent(input: string): MemoryRememberIntent | undefined {
  const match = REMEMBER_PATTERN.exec(input.trim());
  if (!match) return undefined;
  const value = match[2]!.trim().replace(/라고$/u, "").trim();
  if (!value || value.length > 2_000) return undefined;
  const subject = match[1]!;
  for (const category of MEMORY_CATEGORIES) {
    if (matchesCategory(subject, category)) return { key: category.key, label: category.label, value };
  }
  return undefined;
}

export function formatMemoryRememberResult(
  intent: MemoryRememberIntent,
  output: Readonly<Record<string, unknown>>,
): string {
  const action = output.created === false ? "업데이트했습니다" : "저장했습니다";
  return `${escapeMarkdown(intent.label)}를 **${escapeMarkdown(intent.value)}**로 ${action}.`;
}

function isMemoryItem(value: unknown): value is { readonly key: string; readonly value: string } {
  return value !== null && typeof value === "object" && !Array.isArray(value) &&
    "key" in value && typeof value.key === "string" &&
    "value" in value && typeof value.value === "string";
}

function escapeMarkdown(value: string): string {
  return value.replace(/[\\`*_[\]{}()#+.!|>-]/gu, "\\$&");
}

function matchesCategory(text: string, category: MemoryCategory): boolean {
  return category.aliases.some(alias => new RegExp(escapeRegularExpression(alias).replace(/\\ /gu, "\\s*"), "iu").test(text));
}

function escapeRegularExpression(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}
