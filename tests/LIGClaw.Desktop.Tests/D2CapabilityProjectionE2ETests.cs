using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;
using ApplicationCapabilityRuntimeCandidate = LIGClaw.Application.Capabilities.CapabilityRuntimeCandidate;
using ApplicationCapabilityRuntimeHasher = LIGClaw.Application.Capabilities.CapabilityRuntimeHasher;
using ApplicationCapabilityRuntimeSnapshot = LIGClaw.Application.Capabilities.CapabilityRuntimeSnapshot;
using ContractCapabilityRuntimeCandidate = LIGClaw.Contracts.Generated.CapabilityRuntimeCandidate;
using ContractCapabilityRuntimeSnapshot = LIGClaw.Contracts.Generated.CapabilityRuntimeSnapshot;

namespace LIGClaw.Desktop.Tests;

public sealed class D2CapabilityProjectionE2ETests
{
    private const string LocalEndpointSentinel = "https://mail.internal.invalid/v1/";
    private const string LocalProfileIdSentinel = "boundary-mail-profile";
    private const string LocalTokenSentinel = "boundary-token-must-never-cross-the-pipe";
    private const string ConnectionBindingHash =
        "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact(Timeout = 30_000)]
    public async Task ClineSelectsFrozenCapabilityAndExecutesDesktopToolThroughPolicyPipeline()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sidecarPath = Path.Combine(repositoryRoot, "sidecar", "dist", "index.js");
        Assert.True(File.Exists(sidecarPath), "Build the Sidecar before running the D-2 Capability E2E test.");

        var pipeName = $"ligclaw-d2-capability-e2e-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartSidecar(sidecarPath, pipeName, sessionToken);
        ConversationRunController? runController = null;

        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var client = new RpcClient(pipe);
            var ids = new Queue<string>(["d2-capability-conversation", "d2-capability-run"]);
            var toolPolicy = new ToolInvocationPolicy();
            runController = new ConversationRunController(toolPolicy, ids.Dequeue);
            var run = runController.BeginRun();
            var (snapshot, candidate) = CreateSnapshot();
            runController.BindCapabilitySnapshot(run.ConversationId, run.RunId, snapshot);

            var statusTool = new FakeSystemStatusTool();
            var coordinator = new ToolInvocationCoordinator(
                toolPolicy,
                new WindowsToolHost(
                    WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
                    [statusTool]),
                (_, _) => Task.FromResult(false));
            var invocationController = new ConversationToolInvocationController(runController);
            var invocations = new ConcurrentQueue<ToolInvokeParams>();
            var events = new ConcurrentQueue<AgentEvent>();
            var terminal = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var toolPipeline = new TaskCompletionSource<WindowsToolExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            client.NotificationReceived += (_, notification) =>
            {
                if (notification.Params is null) return;
                if (notification.Method == "tool.invoke")
                {
                    var invocation = notification.Params.Value.Deserialize<ToolInvokeParams>(
                        ContentLengthMessageStream.SerializerOptions)!;
                    invocations.Enqueue(invocation);
                    _ = HandleToolAsync(
                        client,
                        invocationController,
                        coordinator,
                        statusTool,
                        invocation,
                        toolPipeline);
                    return;
                }
                if (notification.Method != "agent.event") return;
                var agentEvent = notification.Params.Value.Deserialize<AgentEvent>(
                    ContentLengthMessageStream.SerializerOptions)!;
                events.Enqueue(agentEvent);
                if (agentEvent.Type is "run_completed" or "run_cancelled" or "run_failed")
                    terminal.TrySetResult(agentEvent);
            };

            _ = await client.InvokeAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    ProtocolConstants.ProtocolVersion,
                    "d2-capability-e2e",
                    ProtocolConstants.ContractHash,
                    sessionToken));
            var started = await client.InvokeAsync<ConversationStartResult>(
                "conversation.start",
                new ConversationStartParams(
                    run.ConversationId,
                    run.RunId,
                    "__test_capability_status__",
                    "cline",
                    CapabilitySnapshot: ToContract(snapshot)));

            Assert.True(started.Accepted);
            await Task.WhenAll(terminal.Task, toolPipeline.Task).WaitAsync(TimeSpan.FromSeconds(10));
            var terminalEvent = await terminal.Task;
            var execution = await toolPipeline.Task;
            Assert.Equal("run_completed", terminalEvent.Type);
            Assert.True(execution.Success, execution.Error);

            var recorded = invocations.ToArray();
            Assert.Equal(
                ["capability.select.v1", "system.get_status.v1"],
                recorded.Select(invocation => invocation.Name));
            var selector = recorded[0];
            Assert.Equal("R0", selector.Risk);
            Assert.Null(selector.CapabilityContext);
            Assert.Equal(
                ["capabilityId", "catalogRevision", "connectionBindingHash", "contentHash", "modelToolName", "snapshotHash", "version"],
                selector.Input.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(candidate.ModelToolName, selector.Input["modelToolName"]?.ToString());
            Assert.Equal(candidate.ConnectionBindingHash, selector.Input["connectionBindingHash"]?.ToString());

            var status = recorded[1];
            Assert.Equal("R0", status.Risk);
            Assert.NotNull(status.CapabilityContext);
            Assert.Equal(snapshot.SnapshotHash.Value, status.CapabilityContext.SnapshotHash);
            Assert.Equal(candidate.ModelToolName, status.CapabilityContext.ModelToolName);
            Assert.Equal(candidate.ExactVersion.Id.Value, status.CapabilityContext.CapabilityId);
            Assert.Equal(candidate.ExactVersion.Version.Value, status.CapabilityContext.Version);
            Assert.Equal(candidate.ExactVersion.ContentHash.Value, status.CapabilityContext.ContentHash);
            Assert.Equal(candidate.CatalogRevision, status.CapabilityContext.CatalogRevision);
            Assert.Equal(candidate.ConnectionBindingHash, status.CapabilityContext.ConnectionBindingHash);
            Assert.Equal(1, statusTool.ExecutionCount);
            Assert.Equal(
                "Capability로 확인한 운영체제는 Windows 11입니다.",
                string.Concat(events.Where(item => item.Type == "text_delta").Select(item => item.Text)));
        }
        finally
        {
            _ = runController?.CompleteActiveRun();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task HttpWorkflowProjectionCrossesNamedPipeWithoutDesktopConnectionDetails()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sidecarPath = Path.Combine(repositoryRoot, "sidecar", "dist", "index.js");
        Assert.True(File.Exists(sidecarPath), "Build the Sidecar before running the D-4 boundary E2E test.");

        var (package, snapshot, candidate, connections) = await CreateHttpWorkflowSnapshotAsync();
        var resolvedRequirement = Assert.Single(connections.Requirements);
        Assert.Equal("mail", resolvedRequirement.Slot);
        Assert.Equal("http", resolvedRequirement.Kind);

        using (var manifestDocument = JsonDocument.Parse(package.CanonicalManifestJson))
        {
            var manifest = manifestDocument.RootElement;
            var requirement = Assert.Single(
                manifest.GetProperty("requiredConnections").EnumerateArray().ToArray());
            AssertObjectProperties(requirement, "kind", "slot");
            Assert.Equal("mail", requirement.GetProperty("slot").GetString());
            Assert.Equal("http", requirement.GetProperty("kind").GetString());
            AssertNoDesktopConnectionDetails(
                manifest,
                connections.LocalEndpoint,
                connections.LocalProfileId.Value,
                connections.LocalToken);
        }

        var pipeName = $"ligclaw-d4-connection-boundary-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartSidecar(sidecarPath, pipeName, sessionToken);
        ConversationRunController? runController = null;

        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var client = new RpcClient(pipe);
            var ids = new Queue<string>(["d4-boundary-conversation", "d4-boundary-run"]);
            runController = new ConversationRunController(new ToolInvocationPolicy(), ids.Dequeue);
            var run = runController.BeginRun();
            runController.BindCapabilitySnapshot(run.ConversationId, run.RunId, snapshot);

            var invocationController = new ConversationToolInvocationController(runController);
            var selectorHandled = new TaskCompletionSource<ToolInvokeParams>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var workflowReceived = new TaskCompletionSource<WorkflowInvokeParams>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.NotificationReceived += (_, notification) =>
            {
                if (notification.Params is null) return;
                if (notification.Method == "tool.invoke")
                {
                    var invocation = notification.Params.Value.Deserialize<ToolInvokeParams>(
                        ContentLengthMessageStream.SerializerOptions)!;
                    _ = HandleSelectorAsync(client, invocationController, invocation, selectorHandled);
                    return;
                }
                if (notification.Method == "workflow.invoke")
                {
                    var invocation = notification.Params.Value.Deserialize<WorkflowInvokeParams>(
                        ContentLengthMessageStream.SerializerOptions)!;
                    workflowReceived.TrySetResult(invocation);
                }
            };

            _ = await client.InvokeAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    ProtocolConstants.ProtocolVersion,
                    "d4-connection-boundary-e2e",
                    ProtocolConstants.ContractHash,
                    sessionToken));
            var contractSnapshot = ToContract(snapshot);
            var startParameters = new ConversationStartParams(
                run.ConversationId,
                run.RunId,
                "__test_capability_status__",
                "cline",
                CapabilitySnapshot: contractSnapshot);

            var serializedStart = JsonSerializer.SerializeToElement(
                startParameters,
                ContentLengthMessageStream.SerializerOptions);
            var wireCandidate = Assert.Single(
                serializedStart.GetProperty("capabilitySnapshot").GetProperty("candidates").EnumerateArray().ToArray());
            AssertObjectProperties(
                wireCandidate,
                "canonicalInputSchemaJson",
                "catalogRevision",
                "connectionBindingHash",
                "contentHash",
                "description",
                "displayName",
                "id",
                "instructions",
                "intentExamples",
                "kind",
                "maximumRisk",
                "modelToolName",
                "requiredTools",
                "version");
            Assert.Equal("workflow", wireCandidate.GetProperty("kind").GetString());
            Assert.Equal(ConnectionBindingHash, wireCandidate.GetProperty("connectionBindingHash").GetString());
            AssertNoDesktopConnectionDetails(
                serializedStart,
                connections.LocalEndpoint,
                connections.LocalProfileId.Value,
                connections.LocalToken);

            var started = await client.InvokeAsync<ConversationStartResult>(
                "conversation.start",
                startParameters);
            Assert.True(started.Accepted);

            await Task.WhenAll(selectorHandled.Task, workflowReceived.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            var selector = await selectorHandled.Task;
            Assert.Equal("capability.select.v1", selector.Name);
            AssertObjectProperties(
                JsonSerializer.SerializeToElement(
                    selector.Input,
                    ContentLengthMessageStream.SerializerOptions),
                "capabilityId",
                "catalogRevision",
                "connectionBindingHash",
                "contentHash",
                "modelToolName",
                "snapshotHash",
                "version");
            Assert.Equal(ConnectionBindingHash, selector.Input["connectionBindingHash"]?.ToString());

            var workflow = await workflowReceived.Task;
            Assert.Equal("workflow.invoke.v1", workflow.Name);
            Assert.Empty(workflow.Input);
            Assert.Equal(ConnectionBindingHash, workflow.CapabilityContext.ConnectionBindingHash);
            Assert.Equal(candidate.ExactVersion.Id.Value, workflow.CapabilityContext.CapabilityId);
            var serializedWorkflow = JsonSerializer.SerializeToElement(
                workflow,
                ContentLengthMessageStream.SerializerOptions);
            AssertObjectProperties(
                serializedWorkflow,
                "capabilityContext",
                "conversationId",
                "deadlineUtc",
                "idempotencyKey",
                "input",
                "name",
                "runId",
                "workflowCallId");
            AssertObjectProperties(
                serializedWorkflow.GetProperty("capabilityContext"),
                "capabilityId",
                "catalogRevision",
                "connectionBindingHash",
                "contentHash",
                "modelToolName",
                "snapshotHash",
                "version");
            AssertNoDesktopConnectionDetails(
                serializedWorkflow,
                connections.LocalEndpoint,
                connections.LocalProfileId.Value,
                connections.LocalToken);
        }
        finally
        {
            _ = runController?.CompleteActiveRun();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task HandleToolAsync(
        RpcClient client,
        ConversationToolInvocationController invocationController,
        ToolInvocationCoordinator coordinator,
        FakeSystemStatusTool statusTool,
        ToolInvokeParams invocation,
        TaskCompletionSource<WindowsToolExecutionResult> toolPipeline)
    {
        try
        {
            var outcome = await invocationController.ExecuteAsync(invocation, coordinator.ExecutePreparedAsync);
            if (!outcome.Execution.Success)
                throw new InvalidOperationException(outcome.Execution.Error ?? "Capability Tool execution failed.");
            if (invocation.Name == "capability.select.v1") Assert.Equal(0, statusTool.ExecutionCount);

            var result = await client.InvokeAsync<ToolResultResult>(
                "tool.result",
                new ToolResultParams(
                    invocation.ToolCallId,
                    outcome.Execution.Success,
                    outcome.Execution.Output,
                    outcome.Execution.Error));
            Assert.True(result.Accepted);
            if (invocation.Name == "system.get_status.v1")
                toolPipeline.TrySetResult(outcome.Execution);
        }
        catch (Exception exception)
        {
            toolPipeline.TrySetException(exception);
        }
    }

    private static async Task HandleSelectorAsync(
        RpcClient client,
        ConversationToolInvocationController invocationController,
        ToolInvokeParams invocation,
        TaskCompletionSource<ToolInvokeParams> completion)
    {
        try
        {
            var outcome = await invocationController.ExecuteAsync(invocation, null);
            if (!outcome.Execution.Success)
                throw new InvalidOperationException(outcome.Execution.Error ?? "Capability selection failed.");
            var result = await client.InvokeAsync<ToolResultResult>(
                "tool.result",
                new ToolResultParams(
                    invocation.ToolCallId,
                    outcome.Execution.Success,
                    outcome.Execution.Output,
                    outcome.Execution.Error));
            Assert.True(result.Accepted);
            completion.TrySetResult(invocation);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task<(
        CapabilityVersionPayload Package,
        ApplicationCapabilityRuntimeSnapshot Snapshot,
        ApplicationCapabilityRuntimeCandidate Candidate,
        RecordingConnectionReadiness Connections)> CreateHttpWorkflowSnapshotAsync()
    {
        var capabilityId = CapabilityId.Parse("company.sample.http-workflow");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var package = new CapabilityVersionPayload(
            new CapabilityManifest(
                1,
                capabilityId,
                version,
                "Internal Mail Workflow",
                "Runs an approved internal HTTP mail workflow.",
                CapabilityKind.Workflow,
                ["send an internal mail"],
                ["connection.http.request.v1"],
                [new CapabilityConnectionRequirement("mail", "http")],
                "R3",
                "workflow.json"),
            "{\"description\":\"Runs an approved internal HTTP mail workflow.\",\"displayName\":\"Internal Mail Workflow\",\"entrypoint\":\"workflow.json\",\"id\":\"company.sample.http-workflow\",\"intentExamples\":[\"send an internal mail\"],\"kind\":\"workflow\",\"maximumRisk\":\"R3\",\"requiredConnections\":[{\"kind\":\"http\",\"slot\":\"mail\"}],\"requiredTools\":[\"connection.http.request.v1\"],\"schemaVersion\":1,\"version\":\"1.0.0\"}",
            "Use the approved Desktop-owned workflow.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            null,
            "{\"entryStepId\":\"send\",\"schemaVersion\":1,\"steps\":[{\"arguments\":{\"connectionSlot\":\"mail\",\"method\":\"GET\",\"pathTemplate\":\"/messages\",\"reason\":\"send approved mail\"},\"id\":\"send\",\"next\":\"done\",\"tool\":\"connection.http.request.v1\",\"type\":\"tool.call\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}",
            "[]");
        var exactVersion = new CapabilityVersionRef(
            capabilityId,
            version,
            CapabilityContentHasher.Compute(package));
        var source = new SingleCapabilitySource(new CapabilityRuntimeSourceCandidate(
            exactVersion,
            9,
            package));
        var connections = new RecordingConnectionReadiness();
        var builder = new CapabilityRuntimeSnapshotBuilder(
            source,
            new ConnectionHttpDependencyCatalog(),
            connections);

        var snapshot = await builder.BuildAsync("send an internal mail", CancellationToken.None);
        var candidate = Assert.Single(snapshot.Candidates);
        Assert.Equal(CapabilityKind.Workflow, candidate.Kind);
        Assert.Equal(ConnectionBindingHash, candidate.ConnectionBindingHash);
        return (package, snapshot, candidate, connections);
    }

    private static void AssertObjectProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private static void AssertNoDesktopConnectionDetails(
        JsonElement element,
        params string[] forbiddenValues)
    {
        var forbiddenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "account",
            "auth",
            "authentication",
            "baseUrl",
            "credential",
            "credentialRevision",
            "endpoint",
            "profileId",
            "profileTargetHash",
            "profileTargetRevision",
            "secret",
            "targetHash",
            "token",
        };
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.False(
                        forbiddenProperties.Contains(property.Name),
                        $"Desktop-owned Connection property crossed the boundary: {property.Name}");
                    AssertNoDesktopConnectionDetails(property.Value, forbiddenValues);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertNoDesktopConnectionDetails(item, forbiddenValues);
                break;
            case JsonValueKind.String:
                var value = element.GetString()!;
                foreach (var forbiddenValue in forbiddenValues)
                    Assert.DoesNotContain(forbiddenValue, value, StringComparison.Ordinal);
                break;
        }
    }

    private static (ApplicationCapabilityRuntimeSnapshot Snapshot, ApplicationCapabilityRuntimeCandidate Candidate)
        CreateSnapshot()
    {
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("company.sample.status"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse($"sha256:{new string('a', 64)}"));
        var candidate = new ApplicationCapabilityRuntimeCandidate(
            exactVersion,
            7,
            ApplicationCapabilityRuntimeHasher.ComputeModelToolName(exactVersion),
            "Windows 상태 확인",
            "현재 Windows 상태를 안전하게 확인합니다.",
            CapabilityKind.Prompt,
            ["Windows 상태를 확인해 줘"],
            "현재 운영체제 상태를 간결하게 설명합니다.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            ["system.get_status.v1"],
            $"sha256:{new string('b', 64)}",
            "R0");
        return (ApplicationCapabilityRuntimeSnapshot.Create([candidate]), candidate);
    }

    private static ContractCapabilityRuntimeSnapshot ToContract(ApplicationCapabilityRuntimeSnapshot snapshot) =>
        new(
            snapshot.SchemaVersion,
            snapshot.SnapshotHash.Value,
            snapshot.Candidates.Select(candidate => new ContractCapabilityRuntimeCandidate(
                candidate.ExactVersion.Id.Value,
                candidate.ExactVersion.Version.Value,
                candidate.ExactVersion.ContentHash.Value,
                candidate.CatalogRevision,
                candidate.ModelToolName,
                candidate.DisplayName,
                candidate.Description,
                candidate.Kind == CapabilityKind.Workflow ? "workflow" : "prompt",
                candidate.IntentExamples,
                candidate.Instructions,
                candidate.CanonicalInputSchemaJson,
                candidate.RequiredTools,
                candidate.ConnectionBindingHash,
                candidate.MaximumRisk)).ToArray());

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

    private sealed class SingleCapabilitySource(CapabilityRuntimeSourceCandidate candidate)
        : ICapabilityRuntimeCandidateSource
    {
        public Task<IReadOnlyList<CapabilityRuntimeSourceCandidate>> LoadActiveRuntimeCandidatesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityRuntimeSourceCandidate>>([candidate]);
    }

    private sealed class ConnectionHttpDependencyCatalog : ICapabilityDependencyCatalog
    {
        public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
        {
            dependency = StringComparer.Ordinal.Equals(name, "connection.http.request.v1")
                ? new CapabilityToolDependency(name, "R3")
                : null;
            return dependency is not null;
        }
    }

    private sealed class RecordingConnectionReadiness : ICapabilityConnectionReadinessResolver
    {
        public string LocalEndpoint => LocalEndpointSentinel;
        public ConnectionProfileId LocalProfileId { get; } = ConnectionProfileId.Parse(LocalProfileIdSentinel);
        public string LocalToken => LocalTokenSentinel;
        public IReadOnlyList<CapabilityConnectionRequirement> Requirements { get; private set; } = [];

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef exactVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            Requirements = requirements.ToArray();
            return Task.FromResult(new ConnectionReadinessResolution(
                true,
                ConnectionBindingHash,
                [new ResolvedConnectionBinding(
                    exactVersion,
                    "mail",
                    "http",
                    LocalProfileId,
                    3,
                    $"sha256:{new string('d', 64)}",
                    5,
                    7,
                    $"sha256:{new string('f', 64)}",
                    true,
                    null)]));
        }
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

    private sealed class FakeSystemStatusTool : IWindowsToolAdapter
    {
        public int ExecutionCount { get; private set; }
        public string Name => "system.get_status.v1";
        public string Risk => "R0";
        public WindowsCapability RequiredCapabilities => WindowsCapability.None;
        public int Priority => 0;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?> { ["windowsRelease"] = "Windows 11" }));
        }
    }
}
