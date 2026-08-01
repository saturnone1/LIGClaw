export interface BuiltInToolRegistration {
  readonly desktopName: string;
  readonly textFallback?: true;
}

export const BUILT_IN_TOOL_REGISTRATIONS = {
  system_get_status: { desktopName: "system.get_status.v1" },
  system_get_power_status: { desktopName: "system.get_power_status.v1" },
  system_get_storage_status: { desktopName: "system.get_storage_status.v1" },
  system_get_disk_health: { desktopName: "system.get_disk_health.v1" },
  system_get_security_status: { desktopName: "system.get_security_status.v1" },
  system_get_resource_status: { desktopName: "system.get_resource_status.v1" },
  system_get_process_resource_status: { desktopName: "system.get_process_resource_status.v1" },
  system_get_network_status: { desktopName: "system.get_network_status.v1" },
  system_show_notification: { desktopName: "system.show_notification.v1" },
  system_open_settings: { desktopName: "system.open_settings.v1" },
  system_session_action: { desktopName: "system.session_action.v1" },
  app_list_windows: { desktopName: "app.list_windows.v1" },
  app_search_installed: { desktopName: "app.search_installed.v1" },
  app_launch: { desktopName: "app.launch.v1", textFallback: true },
  app_activate: { desktopName: "app.activate.v1" },
  app_close: { desktopName: "app.close.v1" },
  app_set_window_state: { desktopName: "app.set_window_state.v1" },
  explorer_get_context: { desktopName: "explorer.get_context.v1" },
  file_search: { desktopName: "file.search.v1" },
  file_get_metadata: { desktopName: "file.get_metadata.v1" },
  file_read_text: { desktopName: "file.read_text.v1" },
  file_open: { desktopName: "file.open.v1" },
  file_copy: { desktopName: "file.copy.v1" },
  file_move: { desktopName: "file.move.v1" },
  file_rename: { desktopName: "file.rename.v1" },
  file_recycle: { desktopName: "file.recycle.v1" },
  file_undo: { desktopName: "file.undo.v1" },
  file_create_directory: { desktopName: "file.create_directory.v1" },
  file_write_text: { desktopName: "file.write_text.v1" },
  file_zip_create: { desktopName: "file.zip_create.v1" },
  file_zip_extract: { desktopName: "file.zip_extract.v1" },
  clipboard_read_text: { desktopName: "clipboard.read_text.v1" },
  clipboard_write_text: { desktopName: "clipboard.write_text.v1" },
  memory_remember: { desktopName: "memory.remember.v1", textFallback: true },
  memory_list: { desktopName: "memory.list.v1" },
  memory_forget: { desktopName: "memory.forget.v1" },
  schedule_create: { desktopName: "schedule.create.v1", textFallback: true },
  schedule_list: { desktopName: "schedule.list.v1" },
  schedule_cancel: { desktopName: "schedule.cancel.v1" },
  agent_job_create: { desktopName: "agent_job.create.v1" },
  agent_job_list: { desktopName: "agent_job.list.v1" },
  agent_job_cancel: { desktopName: "agent_job.cancel.v1" },
  agent_job_control: { desktopName: "agent_job.control.v1" },
  subagent_run: { desktopName: "subagent.run.v1" },
  web_fetch: { desktopName: "web.fetch.v1" },
  web_search: { desktopName: "web.search.v1" },
  browser_open: { desktopName: "browser.open.v1" },
  browser_snapshot: { desktopName: "browser.snapshot.v1" },
  uia_inspect: { desktopName: "uia.inspect.v1" },
  uia_invoke: { desktopName: "uia.invoke.v1" },
  uia_set_value: { desktopName: "uia.set_value.v1" },
  uia_send_text: { desktopName: "uia.send_text.v1" },
} as const satisfies Readonly<Record<string, BuiltInToolRegistration>>;

export type BuiltInAgentToolName = keyof typeof BUILT_IN_TOOL_REGISTRATIONS;

export const BUILT_IN_TOOL_CAPABILITIES = Object.values(BUILT_IN_TOOL_REGISTRATIONS)
  .map((registration) => `tool.${registration.desktopName}`);

export function desktopToolName<TName extends BuiltInAgentToolName>(name: TName):
  (typeof BUILT_IN_TOOL_REGISTRATIONS)[TName]["desktopName"] {
  return BUILT_IN_TOOL_REGISTRATIONS[name].desktopName;
}

export const TEXT_FALLBACK_AGENT_TOOL_NAMES = [
  "app_launch",
  "memory_remember",
  "schedule_create",
] as const satisfies readonly BuiltInAgentToolName[];

export type TextFallbackAgentToolName = typeof TEXT_FALLBACK_AGENT_TOOL_NAMES[number];
export type TextFallbackDesktopToolName =
  (typeof BUILT_IN_TOOL_REGISTRATIONS)[TextFallbackAgentToolName]["desktopName"];
