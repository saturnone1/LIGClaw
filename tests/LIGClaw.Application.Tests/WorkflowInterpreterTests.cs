using LIGClaw.Application.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class WorkflowInterpreterTests
{
    private const string EmptyInputSchema =
        "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}";
    private const string PathInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":4096,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}";
    private const string StatusResultSchema =
        "{\"additionalProperties\":false,\"properties\":{\"buildNumber\":{\"type\":\"integer\"},\"windowsRelease\":{\"type\":\"string\"}},\"required\":[\"windowsRelease\",\"buildNumber\"],\"type\":\"object\"}";
    private const string MetadataInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":32767,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}";
    private const string MetadataResultSchema =
        "{\"additionalProperties\":false,\"properties\":{\"name\":{\"maxLength\":260,\"minLength\":1,\"type\":\"string\"},\"path\":{\"maxLength\":32767,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\",\"name\"],\"type\":\"object\"}";
    private const string NotificationInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"message\":{\"maxLength\":255,\"minLength\":1,\"type\":\"string\"},\"reason\":{\"maxLength\":160,\"minLength\":1,\"type\":\"string\"},\"title\":{\"maxLength\":63,\"minLength\":1,\"type\":\"string\"}},\"required\":[\"title\",\"message\",\"reason\"],\"type\":\"object\"}";
    private const string NotificationResultSchema =
        "{\"additionalProperties\":false,\"properties\":{\"shown\":{\"type\":\"boolean\"}},\"required\":[\"shown\"],\"type\":\"object\"}";

    [Fact]
    public async Task ExecutesThreeToolWorkflowWithBoundedImmutablePayloads()
    {
        var plan = Plan(ThreeToolWorkflow(), PathInputSchema, Catalog(),
            ["system.get_status.v1", "file.get_metadata.v1", "system.show_notification.v1"], "R1");
        var executor = new RecordingExecutor(request => request.ToolName switch
        {
            "system.get_status.v1" => Success(Object(
                ("windowsRelease", Text("Windows 11")),
                ("buildNumber", Integer(22631)))),
            "file.get_metadata.v1" => Success(Object(
                ("path", request.Payload.Properties["path"]),
                ("name", Text("report.txt")))),
            "system.show_notification.v1" => Success(Object(("shown", new WorkflowBooleanValue(true)))),
            _ => throw new InvalidOperationException(),
        });
        var run = RunIdentity();

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            PathInputSchema,
            Object(("path", Text("C:\\work\\report.txt"))),
            run,
            executor);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(WorkflowNextAction.None, result.NextAction);
        Assert.Equal(4, result.Trace.Count);
        Assert.Equal(3, executor.Requests.Count);
        Assert.Equal("C:\\work\\report.txt", Assert.IsType<WorkflowStringValue>(
            executor.Requests[1].Payload.Properties["path"]).Value);
        Assert.Equal(WorkflowToolPreparationMode.OneShot, executor.Requests[2].PreparationMode);
        Assert.Equal("Windows 11", Assert.IsType<WorkflowStringValue>(result.Output!.Properties["release"]).Value);
        Assert.Equal("report.txt", Assert.IsType<WorkflowStringValue>(result.Output.Properties["name"]).Value);
        Assert.Equal(71, result.InputHash.Length);
        Assert.Equal(71, result.AuthorizationHash.Length);
        Assert.True(WorkflowRuntimeContractValidator.ValidateAuthorizationHash(
            run, plan, result.InputHash, result.AuthorizationHash));
    }

    [Fact]
    public async Task RetriesOnlyDeclaredReadOnlyObservationAndNeverRetriesUnknownEffect()
    {
        const string observation = """
            {"schemaVersion":1,"entryStepId":"observe","steps":[
              {"id":"observe","type":"tool.call","tool":"test.observe.v1","arguments":{},"maxRetries":2,"next":"done"},
              {"id":"done","type":"output","value":{"value":{"$bind":{"source":"step","stepId":"observe","path":"/value"}}}}
            ]}
            """;
        const string effect = """
            {"schemaVersion":1,"entryStepId":"effect","steps":[
              {"id":"effect","type":"tool.call","tool":"test.effect.v1","arguments":{},"next":"done"},
              {"id":"done","type":"output","value":{}}
            ]}
            """;
        const string valueResult =
            "{\"additionalProperties\":false,\"properties\":{\"value\":{\"maxLength\":65536,\"type\":\"string\"}},\"required\":[\"value\"],\"type\":\"object\"}";
        var observationPlan = Plan(
            observation,
            EmptyInputSchema,
            new FixedCatalog(new WorkflowToolMetadata(
                "test.observe.v1", "R0", EmptyInputSchema, valueResult, true,
                WorkflowToolPreparationMode.None, true, WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation)),
            ["test.observe.v1"],
            "R0");
        var effectPlan = Plan(
            effect,
            EmptyInputSchema,
            new FixedCatalog(new WorkflowToolMetadata(
                "test.effect.v1", "R1", EmptyInputSchema, NotificationResultSchema, true,
                WorkflowToolPreparationMode.OneShot, true, WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect)),
            ["test.effect.v1"],
            "R1");
        var observationAttempts = 0;
        var observationExecutor = new RecordingExecutor(_ => ++observationAttempts == 1
            ? new WorkflowToolExecutionResponse(
                WorkflowToolExecutionDisposition.RetryableFailed,
                ErrorCode: "fixture.retry")
            : Success(Object(("value", Text("ok")))));
        var effectAttempts = 0;
        var effectExecutor = new RecordingExecutor(_ =>
        {
            effectAttempts++;
            return new WorkflowToolExecutionResponse(
                WorkflowToolExecutionDisposition.OutcomeUnknown,
                ErrorCode: "fixture.unknown");
        });

        var observationResult = await new WorkflowInterpreter().ExecuteAsync(
            observationPlan, EmptyInputSchema, Object(), RunIdentity(), observationExecutor);
        var effectResult = await new WorkflowInterpreter().ExecuteAsync(
            effectPlan, EmptyInputSchema, Object(), RunIdentity("workflow-run-effect"), effectExecutor);
        var cancelledEffectResult = await new WorkflowInterpreter().ExecuteAsync(
            effectPlan,
            EmptyInputSchema,
            Object(),
            RunIdentity("workflow-run-cancelled-effect"),
            new RecordingExecutor(_ => new WorkflowToolExecutionResponse(
                WorkflowToolExecutionDisposition.Cancelled,
                ErrorCode: "workflow.cancelled")));

        Assert.Equal(WorkflowRunStatus.Completed, observationResult.Status);
        Assert.Equal(2, observationAttempts);
        Assert.All(observationExecutor.Requests, request => Assert.Equal(2, request.MaximumRetries));
        Assert.Equal(["retryable_failed", "succeeded", "succeeded"], observationResult.Trace.Select(item => item.Status));
        Assert.Equal(WorkflowRunStatus.AttentionRequired, effectResult.Status);
        Assert.Equal(WorkflowNextAction.ReviewOutcome, effectResult.NextAction);
        Assert.Equal(1, effectAttempts);
        Assert.Equal(WorkflowRunStatus.Cancelled, cancelledEffectResult.Status);
        Assert.Equal("cancelled", cancelledEffectResult.Trace[0].Status);
    }

    [Fact]
    public async Task DoesNotRecommendReplayAfterAnEffectSucceeded()
    {
        const string workflow = """
            {"schemaVersion":1,"entryStepId":"notify","steps":[
              {"id":"notify","type":"tool.call","tool":"system.show_notification.v1","arguments":{"title":"Done","message":"Started","reason":"Workflow test"},"next":"observe"},
              {"id":"observe","type":"tool.call","tool":"system.get_status.v1","arguments":{},"next":"done"},
              {"id":"done","type":"output","value":{}}
            ]}
            """;
        var plan = Plan(
            workflow,
            EmptyInputSchema,
            Catalog(),
            ["system.show_notification.v1", "system.get_status.v1"],
            "R1");
        var executor = new RecordingExecutor(request => request.ToolName switch
        {
            "system.show_notification.v1" => Success(Object(("shown", new WorkflowBooleanValue(true)))),
            "system.get_status.v1" => new WorkflowToolExecutionResponse(
                WorkflowToolExecutionDisposition.FailedKnown,
                ErrorCode: "fixture.observation_failed"),
            _ => throw new InvalidOperationException(),
        });

        var failedAfterEffect = await new WorkflowInterpreter().ExecuteAsync(
            plan, EmptyInputSchema, Object(), RunIdentity("workflow-run-after-effect"), executor);
        var invalidEffectOutput = await new WorkflowInterpreter().ExecuteAsync(
            Plan(
                """
                {"schemaVersion":1,"entryStepId":"notify","steps":[
                  {"id":"notify","type":"tool.call","tool":"system.show_notification.v1","arguments":{"title":"Done","message":"Started","reason":"Workflow test"},"next":"done"},
                  {"id":"done","type":"output","value":{}}
                ]}
                """,
                EmptyInputSchema,
                Catalog(),
                ["system.show_notification.v1"],
                "R1"),
            EmptyInputSchema,
            Object(),
            RunIdentity("workflow-run-invalid-effect-output"),
            new RecordingExecutor(_ => Success(Object(("oversized", Text(new string('x', 65_530)))))));

        Assert.Equal(WorkflowRunStatus.AttentionRequired, failedAfterEffect.Status);
        Assert.Equal(WorkflowNextAction.ReviewOutcome, failedAfterEffect.NextAction);
        Assert.Equal(WorkflowRunStatus.AttentionRequired, invalidEffectOutput.Status);
        Assert.Equal(WorkflowNextAction.ReviewOutcome, invalidEffectOutput.NextAction);
    }

    [Fact]
    public async Task RejectsOversizedStepOutputBeforeItCanFlowToAnotherStep()
    {
        const string workflow = """
            {"schemaVersion":1,"entryStepId":"observe","steps":[
              {"id":"observe","type":"tool.call","tool":"test.observe.v1","arguments":{},"next":"done"},
              {"id":"done","type":"output","value":{}}
            ]}
            """;
        const string largeResult =
            "{\"additionalProperties\":false,\"properties\":{\"value\":{\"maxLength\":65536,\"type\":\"string\"}},\"required\":[\"value\"],\"type\":\"object\"}";
        var plan = Plan(
            workflow,
            EmptyInputSchema,
            new FixedCatalog(new WorkflowToolMetadata(
                "test.observe.v1", "R0", EmptyInputSchema, largeResult, true,
                WorkflowToolPreparationMode.None, true, WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation)),
            ["test.observe.v1"],
            "R0");
        var executor = new RecordingExecutor(_ => Success(Object(("value", Text(new string('x', 65_530))))));

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan, EmptyInputSchema, Object(), RunIdentity(), executor);

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal("workflow.tool.output_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task RejectsOversizedInputMapOutputBeforeItCanFlowToAnotherStep()
    {
        var literal = System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Repeat(new string('x', 4_096), 17).ToArray());
        var workflow =
            "{\"schemaVersion\":1,\"entryStepId\":\"map\",\"steps\":[" +
            "{\"id\":\"map\",\"type\":\"input.map\",\"value\":" + literal +
            ",\"saveAs\":\"large\",\"next\":\"done\"}," +
            "{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}";
        var plan = Plan(workflow, EmptyInputSchema, new FixedCatalog(), [], "R0");

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            EmptyInputSchema,
            Object(),
            RunIdentity("workflow-run-large-input-map"),
            new RecordingExecutor(_ => throw new InvalidOperationException()));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal("workflow.input_map.output_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task AppliesInvariantTransformsAndCanonicalBranchSelection()
    {
        const string inputSchema =
            "{\"additionalProperties\":false,\"properties\":{\"date\":{\"maxLength\":35,\"minLength\":20,\"type\":\"string\"},\"enabled\":{\"type\":\"boolean\"},\"number\":{\"maxLength\":64,\"minLength\":1,\"type\":\"string\"}},\"required\":[\"number\",\"date\",\"enabled\"],\"type\":\"object\"}";
        const string workflow = """
            {"schemaVersion":1,"entryStepId":"number","steps":[
              {"id":"number","type":"transform","operation":"toNumber","operands":[{"$bind":{"source":"input","path":"/number"}}],"saveAs":"number_value","next":"choose"},
              {"id":"choose","type":"condition","left":{"$bind":{"source":"input","path":"/enabled"}},"operator":"isTrue","then":"selected","else":"fallback"},
              {"id":"selected","type":"input.map","value":"selected","saveAs":"selected_value","next":"date"},
              {"id":"fallback","type":"input.map","value":"unused","saveAs":"fallback_value","next":"date"},
              {"id":"date","type":"transform","operation":"toDate","operands":[{"$bind":{"source":"input","path":"/date"}}],"saveAs":"date_value","next":"done"},
              {"id":"done","type":"output","value":{"date":{"$bind":{"source":"variable","name":"date_value","path":""}},"number":{"$bind":{"source":"variable","name":"number_value","path":""}}}}
            ]}
            """;
        var plan = Plan(workflow, inputSchema, new FixedCatalog(), [], "R0");

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            inputSchema,
            Object(
                ("number", Text("1.25")),
                ("date", Text("2026-08-02T09:30:00+09:00")),
                ("enabled", new WorkflowBooleanValue(true))),
            RunIdentity(),
            new RecordingExecutor(_ => throw new InvalidOperationException()));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal("1.25", Assert.IsType<WorkflowNumberValue>(result.Output!.Properties["number"]).CanonicalValue);
        Assert.Equal("2026-08-02T00:30:00.0000000Z", Assert.IsType<WorkflowStringValue>(result.Output.Properties["date"]).Value);
        Assert.DoesNotContain(result.Trace, item => item.StepId == "fallback");
    }

    [Fact]
    public async Task OptionalInputExistsConditionTakesElseBranchWhenValueIsAbsent()
    {
        const string optionalInputSchema =
            "{\"additionalProperties\":false,\"properties\":{\"note\":{\"maxLength\":80,\"type\":\"string\"}},\"type\":\"object\"}";
        const string workflow = """
            {"schemaVersion":1,"entryStepId":"choose","steps":[
              {"id":"choose","type":"condition","left":{"$bind":{"source":"input","path":"/note"}},"operator":"exists","then":"present","else":"absent"},
              {"id":"present","type":"input.map","value":"present","saveAs":"present_value","next":"done"},
              {"id":"absent","type":"input.map","value":"absent","saveAs":"absent_value","next":"done"},
              {"id":"done","type":"output","value":{"handled":true}}
            ]}
            """;
        var plan = Plan(workflow, optionalInputSchema, new FixedCatalog(), [], "R0");

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            optionalInputSchema,
            Object(),
            RunIdentity("workflow-run-optional-exists"),
            new RecordingExecutor(_ => throw new InvalidOperationException()));

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(["choose", "absent", "done"], result.Trace.Select(item => item.StepId));
    }

    [Fact]
    public async Task AuthorizationHashBindsExactCapabilityAndNormalizedInput()
    {
        var plan = Plan(ThreeToolWorkflow(), PathInputSchema, Catalog(),
            ["system.get_status.v1", "file.get_metadata.v1", "system.show_notification.v1"], "R1");
        var executor = HappyExecutor();
        var interpreter = new WorkflowInterpreter();

        var first = await interpreter.ExecuteAsync(
            plan, PathInputSchema, Object(("path", Text("C:\\a.txt"))), RunIdentity(), executor);
        var changedInput = await interpreter.ExecuteAsync(
            plan, PathInputSchema, Object(("path", Text("C:\\b.txt"))), RunIdentity("run-2"), HappyExecutor());
        var changedCapability = await interpreter.ExecuteAsync(
            plan,
            PathInputSchema,
            Object(("path", Text("C:\\a.txt"))),
            RunIdentity("run-3", "2.0.0"),
            HappyExecutor());

        Assert.NotEqual(first.InputHash, changedInput.InputHash);
        Assert.NotEqual(first.AuthorizationHash, changedInput.AuthorizationHash);
        Assert.Equal(first.InputHash, changedCapability.InputHash);
        Assert.NotEqual(first.AuthorizationHash, changedCapability.AuthorizationHash);
    }

    private static RecordingExecutor HappyExecutor() => new(request => request.ToolName switch
    {
        "system.get_status.v1" => Success(Object(
            ("windowsRelease", Text("Windows 11")),
            ("buildNumber", Integer(22631)))),
        "file.get_metadata.v1" => Success(Object(
            ("path", request.Payload.Properties["path"]),
            ("name", Text("file.txt")))),
        _ => Success(Object(("shown", new WorkflowBooleanValue(true)))),
    });

    private static WorkflowToolExecutionResponse Success(WorkflowObjectValue output) =>
        new(WorkflowToolExecutionDisposition.Succeeded, output);

    private static ValidatedWorkflowPlan Plan(
        string json,
        string inputSchema,
        IWorkflowToolMetadataCatalog catalog,
        IReadOnlyList<string> requiredTools,
        string maximumRisk)
    {
        var parsed = new WorkflowDefinitionParser().Parse(json);
        Assert.True(parsed.Succeeded, parsed.Issues.FirstOrDefault()?.Code);
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse("company.test.workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "Test Workflow",
            "Runs a deterministic test workflow.",
            CapabilityKind.Workflow,
            ["Run test workflow"],
            requiredTools,
            [],
            maximumRisk,
            "workflow.json");
        var validation = new WorkflowStaticValidator(catalog).Validate(parsed.Definition!, manifest, inputSchema);
        Assert.True(validation.Succeeded, validation.Issues.FirstOrDefault()?.Code);
        return validation.Plan!;
    }

    private static FixedCatalog Catalog() => new(
        new WorkflowToolMetadata(
            "system.get_status.v1", "R0", EmptyInputSchema, StatusResultSchema, true,
            WorkflowToolPreparationMode.None, true, WorkflowRetryClass.ReadOnly,
            WorkflowToolEffectClass.Observation),
        new WorkflowToolMetadata(
            "file.get_metadata.v1", "R0", MetadataInputSchema, MetadataResultSchema, true,
            WorkflowToolPreparationMode.None, true, WorkflowRetryClass.ReadOnly,
            WorkflowToolEffectClass.Observation),
        new WorkflowToolMetadata(
            "system.show_notification.v1", "R1", NotificationInputSchema, NotificationResultSchema, true,
            WorkflowToolPreparationMode.OneShot, true, WorkflowRetryClass.Never,
            WorkflowToolEffectClass.Effect));

    private static WorkflowRunIdentity RunIdentity(
        string workflowRunId = "workflow-run-1",
        string version = "1.0.0") =>
        new(
            workflowRunId,
            "conversation-1",
            "parent-run-1",
            "parent-tool-call-1",
            new CapabilityVersionRef(
                CapabilityId.Parse("company.test.workflow"),
                CapabilitySemanticVersion.Parse(version),
                CapabilityContentHash.Parse("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            1);

    private static WorkflowObjectValue Object(params (string Name, WorkflowValue Value)[] values) =>
        new(values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal));

    private static WorkflowStringValue Text(string value) => new(value);
    private static WorkflowNumberValue Integer(long value) =>
        new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), true);

    private static string ThreeToolWorkflow() => """
        {
          "schemaVersion": 1,
          "entryStepId": "status",
          "steps": [
            {"id":"status","type":"tool.call","tool":"system.get_status.v1","arguments":{},"next":"metadata"},
            {"id":"metadata","type":"tool.call","tool":"file.get_metadata.v1","arguments":{"path":{"$bind":{"source":"input","path":"/path"}}},"next":"notify"},
            {"id":"notify","type":"tool.call","tool":"system.show_notification.v1","arguments":{"title":"상태","message":"완료","reason":"사용자 요청"},"next":"done"},
            {"id":"done","type":"output","value":{"release":{"$bind":{"source":"step","stepId":"status","path":"/windowsRelease"}},"name":{"$bind":{"source":"step","stepId":"metadata","path":"/name"}}}}
          ]
        }
        """;

    private sealed class FixedCatalog(params WorkflowToolMetadata[] metadata) : IWorkflowToolMetadataCatalog
    {
        private readonly IReadOnlyDictionary<string, WorkflowToolMetadata> values =
            metadata.ToDictionary(item => item.CanonicalName, StringComparer.Ordinal);

        public bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata)
        {
            var found = values.TryGetValue(canonicalName, out var value);
            metadata = value;
            return found;
        }
    }

    private sealed class RecordingExecutor(
        Func<WorkflowToolExecutionRequest, WorkflowToolExecutionResponse> execute) : IWorkflowToolExecutor
    {
        public List<WorkflowToolExecutionRequest> Requests { get; } = [];

        public Task<WorkflowToolExecutionResponse> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(execute(request));
        }
    }
}
