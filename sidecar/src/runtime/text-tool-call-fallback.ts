import {
  desktopToolName,
  type TextFallbackDesktopToolName,
} from "./tools/tool-registration.js";

export interface TextToolCallFallback {
  readonly name: TextFallbackDesktopToolName;
  readonly risk: "R1";
  readonly input: Readonly<Record<string, unknown>>;
  readonly successMessage: string;
}

const MAXIMUM_FALLBACK_TEXT_LENGTH = 8_192;

export function detectTextToolCallFallback(
  userInput: string,
  responseText: string,
): TextToolCallFallback | undefined {
  const value = parseJsonObject(responseText);
  if (!value) return undefined;

  if (hasIntent(userInput, /(?:기억|기억해|저장해|remember|memorize)/iu))
    return detectMemoryRemember(value);
  if (hasIntent(userInput, /(?:알림|예약|리마인드|remind|notification)/iu))
    return detectScheduleCreate(value);
  if (hasIntent(userInput, /(?:실행|열어|켜\s*줘|launch|open|start)/iu))
    return detectAppLaunch(value);
  return undefined;
}

function detectMemoryRemember(value: Readonly<Record<string, unknown>>): TextToolCallFallback | undefined {
  if (!hasOnlyKeys(value, ["kind", "key", "value", "sensitivity", "ttlDays", "reason"])) return undefined;
  const kind = enumString(value.kind, ["alias", "preference", "note"]);
  const key = boundedString(value.key, 1, 80);
  const storedValue = boundedString(value.value, 1, 2_000);
  const sensitivity = enumString(value.sensitivity, ["general", "personal", "sensitive"]);
  const reason = boundedString(value.reason, 1, 400);
  const ttlDays = optionalInteger(value.ttlDays, 1, 3_650);
  if (!kind || !key || !storedValue || !sensitivity || !reason || ttlDays === null) return undefined;
  return {
    name: desktopToolName("memory_remember"), risk: "R1", successMessage: "요청한 내용을 기억해 두었습니다.",
    input: { kind, key, value: storedValue, sensitivity, ...(ttlDays === undefined ? {} : { ttlDays }), reason },
  };
}

function detectScheduleCreate(value: Readonly<Record<string, unknown>>): TextToolCallFallback | undefined {
  if (!hasOnlyKeys(value, [
    "title", "message", "startLocal", "timeZoneId", "delayMinutes", "recurrence", "interval",
    "misfirePolicy", "reason",
  ])) return undefined;
  const title = boundedString(value.title, 1, 63);
  const message = boundedString(value.message, 1, 255);
  const reason = boundedString(value.reason, 1, 400);
  const recurrence = value.recurrence === undefined
    ? "once"
    : enumString(value.recurrence, ["once", "daily", "weekly"]);
  const misfirePolicy = value.misfirePolicy === undefined
    ? "run_once_on_resume"
    : enumString(value.misfirePolicy, ["skip", "run_once_on_resume", "ask"]);
  const interval = optionalInteger(value.interval, 1, 365);
  const delayMinutes = optionalInteger(value.delayMinutes, 1, 10_080);
  const startLocal = value.startLocal === undefined ? undefined : boundedString(value.startLocal, 19, 19);
  const timeZoneId = value.timeZoneId === undefined ? undefined : boundedString(value.timeZoneId, 1, 128);
  const hasDelay = delayMinutes !== undefined && delayMinutes !== null;
  const hasAbsolute = startLocal !== undefined && timeZoneId !== undefined;
  if (!title || !message || !reason || !recurrence || !misfirePolicy || interval === null || delayMinutes === null ||
      hasDelay === hasAbsolute) return undefined;
  return {
    name: desktopToolName("schedule_create"), risk: "R1", successMessage: "요청한 알림을 예약했습니다.",
    input: {
      title, message,
      ...(hasDelay ? { delayMinutes } : { startLocal, timeZoneId }),
      recurrence, ...(interval === undefined ? {} : { interval }), misfirePolicy, reason,
    },
  };
}

function detectAppLaunch(value: Readonly<Record<string, unknown>>): TextToolCallFallback | undefined {
  if (!hasOnlyKeys(value, ["appName", "reason"])) return undefined;
  const appName = boundedString(value.appName, 1, 128);
  const reason = boundedString(value.reason, 1, 400);
  if (!appName || !reason) return undefined;
  return {
    name: desktopToolName("app_launch"), risk: "R1", successMessage: `${appName} 실행을 요청했습니다.`,
    input: { appName, reason },
  };
}

function parseJsonObject(text: string): Readonly<Record<string, unknown>> | undefined {
  if (!text || text.length > MAXIMUM_FALLBACK_TEXT_LENGTH) return undefined;
  let candidate = text.trim();
  const fenced = /^```(?:json)?\s*([\s\S]*?)\s*```$/iu.exec(candidate);
  if (fenced) candidate = fenced[1]!.trim();
  if (!candidate.startsWith("{") || !candidate.endsWith("}")) return undefined;
  try {
    const parsed: unknown = JSON.parse(candidate);
    return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
      ? parsed as Readonly<Record<string, unknown>>
      : undefined;
  } catch {
    return undefined;
  }
}

function hasIntent(input: string, pattern: RegExp): boolean {
  return pattern.test(input);
}

function hasOnlyKeys(value: Readonly<Record<string, unknown>>, allowed: readonly string[]): boolean {
  const allowedSet = new Set(allowed);
  return Object.keys(value).every(key => allowedSet.has(key));
}

function boundedString(value: unknown, minimum: number, maximum: number): string | undefined {
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  return trimmed.length >= minimum && trimmed.length <= maximum ? trimmed : undefined;
}

function enumString<T extends string>(value: unknown, allowed: readonly T[]): T | undefined {
  return typeof value === "string" && allowed.includes(value as T) ? value as T : undefined;
}

function optionalInteger(value: unknown, minimum: number, maximum: number): number | undefined | null {
  if (value === undefined || value === null || value === 0) return undefined;
  return Number.isInteger(value) && (value as number) >= minimum && (value as number) <= maximum
    ? value as number
    : null;
}
