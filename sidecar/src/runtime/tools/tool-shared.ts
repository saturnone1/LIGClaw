import type { DesktopToolBridge } from "../desktop-tool-bridge.js";

export type DesktopToolInvoker = Pick<DesktopToolBridge, "invoke">;

export const pathSchema = { type: "string", minLength: 3, maxLength: 32767 } as const;
export const reasonSchema = { type: "string", minLength: 1, maxLength: 160 } as const;
export const pathsSchema = { type: "array", minItems: 1, maxItems: 20, items: pathSchema } as const;
export const fileOpenInputSchema = {
  type: "object", additionalProperties: false, required: ["path", "reason"],
  properties: { path: pathSchema, reason: reasonSchema },
} as const;
export const fileTransferInputSchema = {
  type: "object", additionalProperties: false, required: ["sources", "destinationDirectory", "reason"],
  properties: { sources: pathsSchema, destinationDirectory: pathSchema, reason: reasonSchema },
} as const;
export const filePathReasonInputSchema = {
  type: "object", additionalProperties: false, required: ["path", "reason"],
  properties: { path: pathSchema, reason: reasonSchema },
} as const;

export const windowActionInputSchema = {
  type: "object",
  additionalProperties: false,
  required: ["windowId", "reason"],
  properties: {
    windowId: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 18 },
    reason: { type: "string", minLength: 1, maxLength: 160 },
  },
} as const;

export interface NotificationInput {
  readonly title: string;
  readonly message: string;
  readonly reason: string;
}

export interface AppLaunchInput {
  readonly appName: string;
  readonly reason: string;
}

export interface AppSearchInstalledInput { readonly query: string; }
export interface ExplorerGetContextInput { readonly reason: string; }

export interface WindowActionInput {
  readonly windowId: string;
  readonly reason: string;
}
export interface WindowStateInput extends WindowActionInput { readonly state: "minimize" | "maximize" | "restore"; }
export interface SystemOpenSettingsInput {
  readonly page: "display" | "sound" | "notifications" | "bluetooth" | "network" | "apps" | "storage" | "windows_update" | "privacy";
  readonly reason: string;
}
export interface SystemSessionActionInput { readonly action: "lock" | "sleep"; readonly reason: string; }

export interface FileSearchInput { readonly rootPath: string; readonly pattern: string; }
export interface FileOpenInput { readonly path: string; readonly reason: string; }
export interface FileMetadataInput { readonly path: string; }
export interface FileReadTextInput { readonly path: string; readonly reason: string; }
export interface FileTransferInput { readonly sources: readonly string[]; readonly destinationDirectory: string; readonly reason: string; }
export interface FileRenameInput { readonly path: string; readonly newName: string; readonly reason: string; }
export interface FileRecycleInput { readonly paths: readonly string[]; readonly reason: string; }
export interface FileUndoInput { readonly undoId: string; readonly reason: string; }
export interface FileCreateDirectoryInput { readonly path: string; readonly reason: string; }
export interface FileWriteTextInput { readonly path: string; readonly text: string; readonly reason: string; }
export interface FileZipCreateInput { readonly sourcePaths: readonly string[]; readonly destinationPath: string; readonly reason: string; }
export interface FileZipExtractInput { readonly zipPath: string; readonly destinationPath: string; readonly reason: string; }
export interface ClipboardReadInput { readonly reason: string; }
export interface ClipboardWriteInput { readonly text: string; readonly reason: string; }
export interface MemoryRememberInput {
  readonly kind: "alias" | "preference" | "note";
  readonly key: string;
  readonly value: string;
  readonly sensitivity: "general" | "personal" | "sensitive";
  readonly ttlDays?: number;
  readonly reason: string;
}
export interface MemoryListInput { readonly query?: string; readonly reason: string; }
export interface MemoryForgetInput { readonly memoryId: string; readonly reason: string; }
export interface ScheduleCreateInput {
  readonly title: string;
  readonly message: string;
  readonly startLocal?: string;
  readonly timeZoneId?: string;
  readonly delayMinutes?: number;
  readonly recurrence: "once" | "daily" | "weekly";
  readonly interval?: number;
  readonly misfirePolicy: "skip" | "run_once_on_resume" | "ask";
  readonly reason: string;
}
export interface ScheduleListInput { readonly includeInactive?: boolean; readonly reason: string; }
export interface ScheduleCancelInput { readonly jobId: string; readonly reason: string; }
export interface AgentJobCreateInput {
  readonly title: string;
  readonly prompt: string;
  readonly startLocal?: string;
  readonly timeZoneId?: string;
  readonly delayMinutes?: number;
  readonly recurrence: "once" | "daily" | "weekly";
  readonly interval?: number;
  readonly misfirePolicy: "skip" | "run_once_on_resume" | "ask";
  readonly modelProfileId?: string;
  readonly maxRuntimeSeconds?: number;
  readonly maxAttempts?: number;
  readonly resultMaxCharacters?: number;
  readonly reason: string;
}
export interface AgentJobListInput { readonly includeInactive?: boolean; readonly reason: string; }
export interface AgentJobCancelInput { readonly jobId: string; readonly reason: string; }
export interface AgentJobControlInput extends AgentJobCancelInput { readonly action: "pause" | "resume" | "retry_now"; }
export interface SubagentRunInput {
  readonly tasks: readonly { readonly title: string; readonly prompt: string }[];
  readonly modelProfileId?: string;
  readonly maxRisk: "R0" | "R1";
  readonly maxRuntimeSeconds: number;
  readonly resultMaxCharacters: number;
  readonly reason: string;
}
export interface UiaInspectInput { readonly windowId: string; readonly query?: string; readonly maxElements?: number; readonly reason: string; }
export interface UiaElementInput { readonly elementId: string; readonly reason: string; }
export interface UiaSetValueInput extends UiaElementInput { readonly value: string; }
export interface UiaSendTextInput extends UiaElementInput { readonly text: string; }
export interface WebFetchInput { readonly url: string; readonly reason: string; }
export interface WebSearchInput { readonly query: string; readonly reason: string; }
export interface BrowserOpenInput { readonly url: string; readonly reason: string; }
export interface BrowserSnapshotInput { readonly browserHandle: string; readonly query?: string; readonly maxElements?: number; readonly reason: string; }

export const uiaElementInputSchema = {
  type: "object", additionalProperties: false, required: ["elementId", "reason"],
  properties: { elementId: { type: "string", pattern: "^[0-9a-f]{32}$" }, reason: reasonSchema },
} as const;

export interface DesktopToolContext {
  readonly conversationId?: string;
  readonly runId?: string;
  readonly toolCallId?: string;
  readonly signal?: AbortSignal;
}
