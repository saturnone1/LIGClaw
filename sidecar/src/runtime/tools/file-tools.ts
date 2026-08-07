import type { AgentTool } from "@cline/agents";
import { DESKTOP_TOOL_RESPONSE_TIMEOUT_MS } from "../runtime-policy.js";
import { desktopToolName } from "./tool-registration.js";
import type { DesktopToolContext, DesktopToolInvoker, FileCreateDirectoryInput, FileMetadataInput, FileOpenInput, FileReadTextInput, FileRecycleInput, FileRenameInput, FileSearchInput, FileTransferInput, FileUndoInput, FileWriteTextInput, FileZipCreateInput, FileZipExtractInput } from "./tool-shared.js";
import { fileOpenInputSchema, filePathReasonInputSchema, fileTransferInputSchema, pathSchema, pathsSchema, reasonSchema } from "./tool-shared.js";

export function createFileTools(
  bridge: DesktopToolInvoker,
  conversationId: string,
  runId: string,
): readonly AgentTool[] {
  const invoke = async (
    context: DesktopToolContext,
    name: string,
    risk: "R0" | "R1" | "R2",
    input: Readonly<Record<string, unknown>>,
    timeoutMs = DESKTOP_TOOL_RESPONSE_TIMEOUT_MS,
  ) => await bridge.invoke({
    ...(context.toolCallId ? { toolCallId: context.toolCallId } : {}),
    conversationId,
    runId,
    name,
    risk,
    input,
  }, context.signal, timeoutMs);
  const search: AgentTool<FileSearchInput, Readonly<Record<string, unknown>>> = {
    name: "file_search",
    description: "절대 경로 폴더 아래에서 이름 패턴으로 파일과 폴더를 최대 100개 찾습니다. 파일 내용은 읽지 않습니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["rootPath", "pattern"],
      properties: { rootPath: pathSchema, pattern: { type: "string", minLength: 1, maxLength: 128 } },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileSearchInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_search"), "R0", { rootPath: input.rootPath, pattern: input.pattern });
    },
  };
  const open: AgentTool<FileOpenInput, Readonly<Record<string, unknown>>> = {
    name: "file_open",
    description: "사용자 승인을 받은 뒤 절대 경로의 기존 파일 또는 폴더 하나를 Windows 기본 앱으로 엽니다.",
    inputSchema: fileOpenInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileOpenInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_open"), "R1", { path: input.path, reason: input.reason });
    },
  };
  const metadata: AgentTool<FileMetadataInput, Readonly<Record<string, unknown>>> = {
    name: "file_get_metadata",
    description: "절대 경로의 기존 파일 또는 폴더 하나에서 이름, 종류, 크기, 수정 시각, 읽기 전용 여부만 확인합니다. 파일 내용은 읽지 않습니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["path"], properties: { path: pathSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileMetadataInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_get_metadata"), "R0", { path: input.path });
    },
  };
  const readText: AgentTool<FileReadTextInput, Readonly<Record<string, unknown>>> = {
    name: "file_read_text",
    description: "사용자 승인 뒤 절대 경로 파일 하나의 UTF 텍스트를 최대 128 KiB 읽습니다. 바이너리는 거부하고 내용은 진단이나 감사 기록에 저장하지 않습니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["path", "reason"],
      properties: { path: pathSchema, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileReadTextInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_read_text"), "R1", { path: input.path, reason: input.reason });
    },
  };
  const copy: AgentTool<FileTransferInput, Readonly<Record<string, unknown>>> = {
    name: "file_copy",
    description: "명시적 승인 뒤 최대 20개 파일을 기존 폴더로 복사합니다. 같은 이름을 덮어쓰지 않으며 항목별 결과를 반환합니다.",
    inputSchema: fileTransferInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileTransferInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_copy"), "R2", { sources: input.sources, destinationDirectory: input.destinationDirectory, reason: input.reason }, 60_000);
    },
  };
  const move: AgentTool<FileTransferInput, Readonly<Record<string, unknown>>> = {
    name: "file_move",
    description: "명시적 승인 뒤 최대 20개 파일을 기존 폴더로 이동합니다. 덮어쓰지 않고 성공 항목은 undo 기록을 만듭니다.",
    inputSchema: fileTransferInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileTransferInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_move"), "R2", { sources: input.sources, destinationDirectory: input.destinationDirectory, reason: input.reason }, 60_000);
    },
  };
  const rename: AgentTool<FileRenameInput, Readonly<Record<string, unknown>>> = {
    name: "file_rename",
    description: "명시적 승인 뒤 기존 파일 하나의 이름을 같은 폴더 안에서 바꿉니다. 덮어쓰지 않고 undo 기록을 만듭니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["path", "newName", "reason"],
      properties: { path: pathSchema, newName: { type: "string", minLength: 1, maxLength: 255 }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileRenameInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_rename"), "R2", { path: input.path, newName: input.newName, reason: input.reason });
    },
  };
  const recycle: AgentTool<FileRecycleInput, Readonly<Record<string, unknown>>> = {
    name: "file_recycle",
    description: "명시적 승인 뒤 최대 20개 기존 파일 또는 폴더를 Windows 휴지통으로 이동합니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["paths", "reason"],
      properties: { paths: pathsSchema, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileRecycleInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_recycle"), "R2", { paths: input.paths, reason: input.reason }, 60_000);
    },
  };
  const undo: AgentTool<FileUndoInput, Readonly<Record<string, unknown>>> = {
    name: "file_undo",
    description: "명시적 승인 뒤 LIGClaw가 발급한 undo ID의 파일 작업 하나를 대상이 변경되지 않은 경우에만 되돌립니다.",
    inputSchema: {
      type: "object", additionalProperties: false, required: ["undoId", "reason"],
      properties: { undoId: { type: "string", pattern: "^[0-9a-f]{32}$" }, reason: reasonSchema },
    },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileUndoInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_undo"), "R2", { undoId: input.undoId, reason: input.reason });
    },
  };
  const createDirectory: AgentTool<FileCreateDirectoryInput, Readonly<Record<string, unknown>>> = {
    name: "file_create_directory",
    description: "사용자 승인 뒤 보호되지 않은 절대 경로에 새 폴더 하나를 만듭니다. 기존 항목을 덮어쓰지 않고 undo 기록을 만듭니다.",
    inputSchema: filePathReasonInputSchema,
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileCreateDirectoryInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_create_directory"), "R1", { path: input.path, reason: input.reason });
    },
  };
  const writeText: AgentTool<FileWriteTextInput, Readonly<Record<string, unknown>>> = {
    name: "file_write_text",
    description: "명시적 승인 뒤 최대 131,072자의 UTF-8 텍스트를 새 파일에 씁니다. 내용은 승인·감사 화면에 표시하지 않고 기존 파일을 덮어쓰지 않습니다.",
    inputSchema: { type: "object", additionalProperties: false, required: ["path", "text", "reason"], properties: { path: pathSchema, text: { type: "string", maxLength: 131072 }, reason: reasonSchema } },
    timeoutMs: DESKTOP_TOOL_RESPONSE_TIMEOUT_MS, retryable: false,
    async execute(input: FileWriteTextInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_write_text"), "R2", { path: input.path, text: input.text, reason: input.reason });
    },
  };
  const zipCreate: AgentTool<FileZipCreateInput, Readonly<Record<string, unknown>>> = {
    name: "file_zip_create",
    description: "명시적 승인 뒤 최대 20개·합계 256 MiB의 기존 파일을 새 ZIP으로 압축합니다. 디렉터리와 덮어쓰기는 지원하지 않습니다.",
    inputSchema: { type: "object", additionalProperties: false, required: ["sourcePaths", "destinationPath", "reason"], properties: { sourcePaths: pathsSchema, destinationPath: pathSchema, reason: reasonSchema } },
    timeoutMs: 120_000, retryable: false,
    async execute(input: FileZipCreateInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_zip_create"), "R2", { sourcePaths: input.sourcePaths, destinationPath: input.destinationPath, reason: input.reason }, 120_000);
    },
  };
  const zipExtract: AgentTool<FileZipExtractInput, Readonly<Record<string, unknown>>> = {
    name: "file_zip_extract",
    description: "명시적 승인 뒤 ZIP을 새 폴더에 풉니다. 경로 이탈·심볼릭 링크·500개 또는 512 MiB 초과 archive와 덮어쓰기를 거부합니다.",
    inputSchema: { type: "object", additionalProperties: false, required: ["zipPath", "destinationPath", "reason"], properties: { zipPath: pathSchema, destinationPath: pathSchema, reason: reasonSchema } },
    timeoutMs: 120_000, retryable: false,
    async execute(input: FileZipExtractInput, context: DesktopToolContext) {
      return await invoke(context, desktopToolName("file_zip_extract"), "R2", { zipPath: input.zipPath, destinationPath: input.destinationPath, reason: input.reason }, 120_000);
    },
  };
  return [search, metadata, readText, open, copy, move, rename, recycle, undo, createDirectory, writeText, zipCreate, zipExtract];
}
