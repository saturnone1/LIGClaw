using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Sidecar.IntegrationTests;

public sealed class SidecarConversationTests
{
    [Fact(Timeout = 30_000)]
    public async Task StreamsReplayAndClineRunsAndCancelsActiveRun()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sidecarPath = Path.Combine(repositoryRoot, "sidecar", "dist", "index.js");
        Assert.True(File.Exists(sidecarPath), "Build the Sidecar before running integration tests.");

        var pipeName = $"ligclaw-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartSidecar(sidecarPath, pipeName, sessionToken);

        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var client = new RpcClient(pipe);
            var events = new ConcurrentQueue<AgentEvent>();
            var toolInvocations = new ConcurrentQueue<ToolInvokeParams>();
            var terminalEvents = new ConcurrentDictionary<string, TaskCompletionSource<AgentEvent>>();
            client.NotificationReceived += (_, notification) =>
            {
                if (notification.Params is null) return;
                if (notification.Method == "tool.invoke")
                {
                    var invocation = notification.Params.Value.Deserialize<ToolInvokeParams>(ContentLengthMessageStream.SerializerOptions)!;
                    toolInvocations.Enqueue(invocation);
                    IReadOnlyDictionary<string, object?> output = invocation.Name switch
                    {
                        "system.get_status.v1" => new Dictionary<string, object?> { ["windowsRelease"] = "Windows 10" },
                        "app.list_windows.v1" => new Dictionary<string, object?>
                        {
                            ["windows"] = new object?[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["windowId"] = "0x1",
                                    ["title"] = "Inbox",
                                    ["processName"] = "mail",
                                    ["isForeground"] = true,
                                    ["isMinimized"] = false,
                                },
                                new Dictionary<string, object?>
                                {
                                    ["windowId"] = "0x2",
                                    ["title"] = "Notes",
                                    ["processName"] = "notepad",
                                    ["isForeground"] = false,
                                    ["isMinimized"] = true,
                                },
                            },
                        },
                        "app.search_installed.v1" => new Dictionary<string, object?>
                        {
                            ["apps"] = new object?[] { new Dictionary<string, object?> { ["displayName"] = "계산기" } },
                            ["truncated"] = false,
                        },
                        "explorer.get_context.v1" => new Dictionary<string, object?>
                        {
                            ["folderPath"] = "C:\\Work",
                            ["selectedItems"] = new object?[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["path"] = "C:\\Work\\report.docx",
                                    ["name"] = "report.docx",
                                    ["isDirectory"] = false,
                                },
                            },
                            ["selectionTruncated"] = false,
                        },
                        "file.get_metadata.v1" => new Dictionary<string, object?>
                        {
                            ["path"] = "C:\\Work\\report.txt",
                            ["name"] = "report.txt",
                            ["isDirectory"] = false,
                            ["sizeBytes"] = 6,
                            ["lastWriteTimeUtc"] = "2026-07-26T00:00:00Z",
                            ["readOnly"] = false,
                        },
                        "file.read_text.v1" => new Dictionary<string, object?>
                        {
                            ["text"] = "report",
                            ["length"] = 6,
                            ["truncated"] = false,
                        },
                        "memory.remember.v1" => new Dictionary<string, object?>
                        {
                            ["memoryId"] = new string('a', 32),
                            ["created"] = true,
                        },
                        "schedule.create.v1" => new Dictionary<string, object?>
                        {
                            ["jobId"] = new string('b', 32),
                            ["nextRunAtUtc"] = "2026-07-27T00:00:00Z",
                            ["status"] = "pending",
                        },
                        "system.show_notification.v1" => new Dictionary<string, object?> { ["shown"] = true },
                        "app.launch.v1" => new Dictionary<string, object?>
                        {
                            ["launched"] = true,
                            ["displayName"] = "Calculator",
                        },
                        "file.move.v1" => new Dictionary<string, object?>
                        {
                            ["succeeded"] = 1,
                            ["failed"] = 0,
                            ["items"] = Array.Empty<object>(),
                        },
                        _ => new Dictionary<string, object?>(),
                    };
                    _ = client.InvokeAsync<ToolResultResult>(
                        "tool.result",
                        new ToolResultParams(
                            invocation.ToolCallId,
                            true,
                            output,
                            null));
                    return;
                }
                if (notification.Method != "agent.event") return;
                var agentEvent = notification.Params.Value.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)!;
                events.Enqueue(agentEvent);
                if (agentEvent.Type is "run_completed" or "run_cancelled" or "run_failed")
                    terminalEvents.GetOrAdd(agentEvent.ConversationId, _ => NewCompletion()).TrySetResult(agentEvent);
            };

            var initialized = await client.InvokeAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    ProtocolConstants.ProtocolVersion,
                    "integration-test",
                    ProtocolConstants.ContractHash,
                    sessionToken));
            Assert.Contains("agent.events", initialized.Capabilities);
            Assert.Contains("runtime.cline.0.0.65", initialized.Capabilities);
            Assert.Contains("provider.configure", initialized.Capabilities);
            Assert.Contains("provider.test", initialized.Capabilities);
            Assert.Contains("mcp.configure", initialized.Capabilities);
            Assert.Contains("mcp.status", initialized.Capabilities);
            Assert.Contains("tool.system.get_status.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_power_status.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_process_resource_status.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_network_details.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_device_status.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_storage_status.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_disk_health.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_security_status.v1", initialized.Capabilities);
            Assert.Contains("tool.file.create_directory.v1", initialized.Capabilities);
            Assert.Contains("tool.file.write_text.v1", initialized.Capabilities);
            Assert.Contains("tool.file.zip_create.v1", initialized.Capabilities);
            Assert.Contains("tool.file.zip_extract.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_resource_status.v1", initialized.Capabilities);
            Assert.Contains("tool.system.get_network_status.v1", initialized.Capabilities);
            Assert.Contains("tool.app.list_windows.v1", initialized.Capabilities);
            Assert.Contains("tool.app.search_installed.v1", initialized.Capabilities);
            Assert.Contains("tool.system.show_notification.v1", initialized.Capabilities);
            Assert.Contains("tool.app.launch.v1", initialized.Capabilities);
            Assert.Contains("tool.app.activate.v1", initialized.Capabilities);
            Assert.Contains("tool.app.close.v1", initialized.Capabilities);
            Assert.Contains("tool.app.set_window_state.v1", initialized.Capabilities);
            Assert.Contains("tool.system.open_settings.v1", initialized.Capabilities);
            Assert.Contains("tool.system.session_action.v1", initialized.Capabilities);
            Assert.Contains("tool.explorer.get_context.v1", initialized.Capabilities);
            Assert.Contains("tool.file.search.v1", initialized.Capabilities);
            Assert.Contains("tool.file.get_metadata.v1", initialized.Capabilities);
            Assert.Contains("tool.file.read_text.v1", initialized.Capabilities);
            Assert.Contains("tool.file.open.v1", initialized.Capabilities);
            Assert.Contains("tool.file.copy.v1", initialized.Capabilities);
            Assert.Contains("tool.file.move.v1", initialized.Capabilities);
            Assert.Contains("tool.file.rename.v1", initialized.Capabilities);
            Assert.Contains("tool.file.recycle.v1", initialized.Capabilities);
            Assert.Contains("tool.file.undo.v1", initialized.Capabilities);
            Assert.Contains("tool.clipboard.read_text.v1", initialized.Capabilities);
            Assert.Contains("tool.clipboard.write_text.v1", initialized.Capabilities);
            Assert.Contains("tool.memory.remember.v1", initialized.Capabilities);
            Assert.Contains("tool.memory.list.v1", initialized.Capabilities);
            Assert.Contains("tool.memory.forget.v1", initialized.Capabilities);
            Assert.Contains("tool.schedule.create.v1", initialized.Capabilities);
            Assert.Contains("tool.schedule.list.v1", initialized.Capabilities);
            Assert.Contains("tool.schedule.cancel.v1", initialized.Capabilities);
            Assert.Contains("tool.agent_job.control.v1", initialized.Capabilities);

            var configured = await client.InvokeAsync<ProviderConfigureResult>(
                "provider.configure",
                new ProviderConfigureParams("http://127.0.0.1:1/v1", "test-key", "test-model"));
            Assert.True(configured.Configured);
            var connectionTest = await client.InvokeAsync<ProviderTestResult>(
                "provider.test",
                new ProviderConfigureParams("http://127.0.0.1:1/v1", "test-key", "test-model"));
            Assert.False(connectionTest.Success);
            var mcpConfigured = await client.InvokeAsync<McpConfigureResult>(
                "mcp.configure",
                new McpConfigureParams(Array.Empty<IReadOnlyDictionary<string, object?>>()));
            Assert.Empty(mcpConfigured.Connections);
            var mcpStatus = await client.InvokeAsync<McpStatusResult>("mcp.status", new McpStatusParams());
            Assert.Empty(mcpStatus.Connections);

            await Assert.ThrowsAsync<RpcException>(() =>
                client.InvokeAsync<ConversationCancelResult>("conversation.cancel", null));
            await Assert.ThrowsAsync<RpcException>(() =>
                client.InvokeAsync<ToolResultResult>("tool.result", new
                {
                    toolCallId = "invalid-output",
                    success = true,
                    output = Array.Empty<object>(),
                }));
            _ = await client.InvokeAsync<PingResult>("ping", null);

            await RunAndAssertAsync(client, events, terminalEvents, "replay-conversation", "replay", "hello replay", "Replay runtime received: hello replay");
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "cline-conversation",
                "cline",
                "hello cline",
                "요청을 확인했어요.\n\n“hello cline”\n\n현재는 대화 연결을 준비하는 단계예요. Windows 작업 기능이 연결되면 이 요청을 직접 처리할 수 있어요.");
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "restored-context-conversation",
                "cline",
                "__test_conversation_context__",
                "이전 사용자 메시지: 이전 질문",
                [
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "이전 질문" },
                    new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = "이전 답변" },
                ]);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "tool-conversation",
                "cline",
                "__test_system_status__",
                "현재 운영체제는 Windows 10입니다.");
            var statusInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "system.get_status.v1");
            Assert.Equal("R0", statusInvocation.Risk);
            Assert.Equal("tool-conversation", statusInvocation.ConversationId);
            Assert.Equal("tool-conversation-run", statusInvocation.RunId);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "window-tool-conversation",
                "cline",
                "__test_list_windows__",
                "열린 앱 창은 2개입니다.");
            var windowInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "app.list_windows.v1");
            Assert.Equal("R0", windowInvocation.Risk);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "app-search-conversation",
                "cline",
                "__test_app_search__",
                "설치 앱 1개를 찾았습니다.");
            var appSearchInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "app.search_installed.v1");
            Assert.Equal("R0", appSearchInvocation.Risk);
            Assert.Equal("계산", appSearchInvocation.Input["query"]?.ToString());
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "explorer-context-conversation",
                "cline",
                "__test_explorer_context__",
                "파일 탐색기 선택 항목은 1개입니다.");
            var explorerInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "explorer.get_context.v1");
            Assert.Equal("R1", explorerInvocation.Risk);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "file-metadata-conversation",
                "cline",
                "__test_file_metadata__",
                "파일 메타데이터를 확인했습니다.");
            Assert.Equal("R0", Assert.Single(toolInvocations, invocation => invocation.Name == "file.get_metadata.v1").Risk);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "file-read-conversation",
                "cline",
                "__test_file_read_text__",
                "승인된 파일 텍스트를 읽었습니다.");
            Assert.Equal("R1", Assert.Single(toolInvocations, invocation => invocation.Name == "file.read_text.v1").Risk);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "memory-conversation",
                "cline",
                "__test_memory_remember__",
                "승인한 내용을 기억했습니다.");
            var memoryInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "memory.remember.v1");
            Assert.Equal("R1", memoryInvocation.Risk);
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "schedule-conversation",
                "cline",
                "__test_schedule_create__",
                "승인한 반복 알림을 예약했습니다.");
            var scheduleInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "schedule.create.v1");
            Assert.Equal("R1", scheduleInvocation.Risk);
            Assert.Equal("weekly", scheduleInvocation.Input["recurrence"]?.ToString());
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "notification-tool-conversation",
                "cline",
                "__test_show_notification__",
                "승인한 알림을 표시했습니다.");
            var notificationInvocation = Assert.Single(
                toolInvocations,
                invocation => invocation.Name == "system.show_notification.v1");
            Assert.Equal("R1", notificationInvocation.Risk);
            Assert.Equal("LIGClaw 테스트", notificationInvocation.Input["title"]?.ToString());
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "app-launch-conversation",
                "cline",
                "__test_launch_app__",
                "Calculator 앱을 실행했습니다.");
            var appLaunchInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "app.launch.v1");
            Assert.Equal("R1", appLaunchInvocation.Risk);
            Assert.Equal("app-launch-conversation", appLaunchInvocation.ConversationId);
            Assert.Equal("app-launch-conversation-run", appLaunchInvocation.RunId);
            Assert.Equal("Calculator", appLaunchInvocation.Input["appName"]?.ToString());
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "file-move-conversation",
                "cline",
                "__test_move_file__",
                "파일 1개를 이동했습니다.");
            var fileMoveInvocation = Assert.Single(toolInvocations, invocation => invocation.Name == "file.move.v1");
            Assert.Equal("R2", fileMoveInvocation.Risk);
            Assert.Equal("file-move-conversation-run", fileMoveInvocation.RunId);

            var cancelConversationId = "cancel-conversation";
            var cancellationTerminal = terminalEvents.GetOrAdd(cancelConversationId, _ => NewCompletion());
            var started = await client.InvokeAsync<ConversationStartResult>(
                "conversation.start",
                new ConversationStartParams(cancelConversationId, "cancel-run", new string('x', 2_000), "replay", null, null));
            Assert.True(started.Accepted);
            Assert.Equal("cancel-run", started.RunId);
            var cancelled = await client.InvokeAsync<ConversationCancelResult>(
                "conversation.cancel",
                new ConversationCancelParams(cancelConversationId));
            Assert.True(cancelled.Cancelled);
            Assert.Equal("run_cancelled", (await cancellationTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task RunAndAssertAsync(
        RpcClient client,
        ConcurrentQueue<AgentEvent> events,
        ConcurrentDictionary<string, TaskCompletionSource<AgentEvent>> terminals,
        string conversationId,
        string runtime,
        string input,
        string expectedOutput,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null)
    {
        var terminal = terminals.GetOrAdd(conversationId, _ => NewCompletion());
        var started = await client.InvokeAsync<ConversationStartResult>(
            "conversation.start",
            new ConversationStartParams(conversationId, $"{conversationId}-run", input, runtime, history, null));
        Assert.True(started.Accepted);
        Assert.Equal($"{conversationId}-run", started.RunId);
        Assert.Equal("run_completed", (await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);

        var runEvents = events.Where(agentEvent => agentEvent.ConversationId == conversationId).ToArray();
        Assert.Equal(Enumerable.Range(0, runEvents.Length).Select(value => (long)value), runEvents.Select(agentEvent => agentEvent.Sequence));
        Assert.Equal("run_started", runEvents[0].Type);
        Assert.Equal("run_completed", runEvents[^1].Type);
        Assert.Equal(expectedOutput, string.Concat(runEvents.Where(agentEvent => agentEvent.Type == "text_delta").Select(agentEvent => agentEvent.Text)));
    }

    private static TaskCompletionSource<AgentEvent> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Process StartSidecar(string sidecarPath, string pipeName, string sessionToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("LIGCLAW_NODE_PATH") ?? "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(sidecarPath)!,
        };
        startInfo.ArgumentList.Add(sidecarPath);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.Environment["LIGCLAW_SESSION_TOKEN"] = sessionToken;
        startInfo.Environment["LIGCLAW_TEST_DETERMINISTIC"] = "1";
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Sidecar.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LIGClaw.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
