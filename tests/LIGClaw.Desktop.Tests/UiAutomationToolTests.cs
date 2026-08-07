using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class UiAutomationToolTests
{
    [Fact]
    public async Task InspectReturnsBoundedDesktopIssuedElementsOnlyAfterApproval()
    {
        var service = new FakeUiAutomationService
        {
            Inspection = new UiAutomationInspection(
                "0x123", "Fixture", "fixture",
                [new UiAutomationElementSummary("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "저장", "SaveButton", "Button", true, false, true, false, false)],
                true),
        };
        service.Window = TargetWindow();
        var host = new WindowsToolHost(Profile(), [new UiaInspectTool(service)]);
        var invocation = InspectInvocation();

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(result.Success);
        Assert.Equal(30, service.MaximumElements);
        var element = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["elements"]));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", element["elementId"]);
        Assert.Equal(true, element["supportsInvoke"]);
        Assert.Equal(true, result.Output["truncated"]);
    }

    [Fact]
    public async Task InspectRejectsAWindowWhoseIdentityChangedAfterApproval()
    {
        var service = new FakeUiAutomationService { Window = TargetWindow() };
        var host = new WindowsToolHost(Profile(), [new UiaInspectTool(service)]);
        var invocation = InspectInvocation();

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        service.Window = TargetWindow() with { Title = "Changed" };
        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
        Assert.Equal(0, service.InspectCount);
    }

    [Fact]
    public async Task InvokeRevalidatesTheElementIdentityBeforeActing()
    {
        var original = ElementTarget();
        var service = new FakeUiAutomationService { Target = original };
        var host = new WindowsToolHost(Profile(), [new UiaInvokeTool(service)]);
        var invocation = ActionInvocation("uia.invoke.v1", "R2");

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        service.Target = original with { Name = "삭제" };
        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
        Assert.Equal(0, service.InvokeCount);
    }

    [Fact]
    public async Task SetValueAndSendTextUseOnlyTheApprovedExactPayload()
    {
        var service = new FakeUiAutomationService { Target = ElementTarget() with { SupportsValue = true } };
        var setHost = new WindowsToolHost(Profile(), [new UiaSetValueTool(service)]);
        var setInput = new Dictionary<string, object?>
        {
            ["elementId"] = service.Target.ElementId,
            ["value"] = "승인한 값",
            ["reason"] = "양식을 채우기 위해",
        };
        var setInvocation = new ToolInvokeParams("set", "conversation", "run", "uia.set_value.v1", "R2", setInput);

        Assert.True(setHost.CreateApprovalPrompt(setInvocation).Success);
        Assert.True((await setHost.ExecuteAsync(setInvocation)).Success);
        Assert.Equal("승인한 값", service.SetValue);

        service.Target = service.Target with { SupportsValue = false };
        var sendHost = new WindowsToolHost(Profile(), [new UiaSendTextTool(service)]);
        var sendInput = new Dictionary<string, object?>
        {
            ["elementId"] = service.Target.ElementId,
            ["text"] = "일반 텍스트",
            ["reason"] = "입력 요소를 채우기 위해",
        };
        var sendInvocation = new ToolInvokeParams("send", "conversation", "run", "uia.send_text.v1", "R2", sendInput);

        Assert.True(sendHost.CreateApprovalPrompt(sendInvocation).Success);
        Assert.True((await sendHost.ExecuteAsync(sendInvocation)).Success);
        Assert.Equal("일반 텍스트", service.SentText);
    }

    [Fact]
    public async Task SetValueCanClearAnEditableElement()
    {
        var service = new FakeUiAutomationService { Target = ElementTarget() with { SupportsValue = true } };
        var host = new WindowsToolHost(Profile(), [new UiaSetValueTool(service)]);
        var invocation = ActionInvocation("uia.set_value.v1", "R2", "value", string.Empty);

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        Assert.True((await host.ExecuteAsync(invocation)).Success);
        Assert.Equal(string.Empty, service.SetValue);
    }

    [Fact]
    public async Task SetValueCannotSubstituteAPayloadAfterApproval()
    {
        var service = new FakeUiAutomationService { Target = ElementTarget() with { SupportsValue = true } };
        var host = new WindowsToolHost(Profile(), [new UiaSetValueTool(service)]);
        var approved = ActionInvocation("uia.set_value.v1", "R2", "value", "승인한 값");
        var changed = ActionInvocation("uia.set_value.v1", "R2", "value", "바뀐 값") with { ToolCallId = approved.ToolCallId };

        Assert.True(host.CreateApprovalPrompt(approved).Success);
        Assert.False((await host.ExecuteAsync(changed)).Success);
        Assert.Null(service.SetValue);
    }

    [Fact]
    public async Task PasswordElementsAreRejectedBeforeApprovalAndExecution()
    {
        var service = new FakeUiAutomationService { Target = ElementTarget() with { IsPassword = true, SupportsValue = true } };
        var host = new WindowsToolHost(Profile(), [new UiaSetValueTool(service)]);
        var input = new Dictionary<string, object?>
        {
            ["elementId"] = service.Target.ElementId,
            ["value"] = "not-stored",
            ["reason"] = "비밀번호를 입력하기 위해",
        };
        var invocation = new ToolInvokeParams("set", "conversation", "run", "uia.set_value.v1", "R2", input);

        Assert.False(host.CreateApprovalPrompt(invocation).Success);
        Assert.False((await host.ExecuteAsync(invocation)).Success);
        Assert.Null(service.SetValue);
    }

    [Fact]
    public async Task SendTextReportsUserInputInterruptionWithoutTyping()
    {
        var service = new FakeUiAutomationService
        {
            Target = ElementTarget(),
            SendStatus = UiAutomationActionStatus.UserInputDetected,
        };
        var host = new WindowsToolHost(Profile(), [new UiaSendTextTool(service)]);
        var invocation = ActionInvocation("uia.send_text.v1", "R2", "text", "중단할 텍스트");

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
        Assert.Contains("사용자 입력", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UiTextPayloadIsShownForApprovalButNeverWrittenToAuditSummaries()
    {
        const string privateValue = "private-form-value";
        var service = new FakeUiAutomationService { Target = ElementTarget() with { SupportsValue = true } };
        var host = new WindowsToolHost(Profile(), [new UiaSetValueTool(service)]);
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");
        var audit = new FakeAuditSink();
        var coordinator = new ToolInvocationCoordinator(policy, host, (prompt, _) =>
        {
            Assert.Contains(privateValue, prompt.Details, StringComparison.Ordinal);
            return Task.FromResult(true);
        }, audit);
        var invocation = ActionInvocation("uia.set_value.v1", "R2", "value", privateValue);

        Assert.True((await coordinator.ExecuteAsync(invocation)).Success);

        Assert.DoesNotContain(privateValue, Assert.Single(audit.Approvals).Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(privateValue, Assert.Single(audit.Executions).Summary, StringComparison.Ordinal);
    }

    private static WindowsPlatformProfile Profile() =>
        WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);

    private static UiAutomationWindowTarget TargetWindow() => new("0x123", 42, 123456, "Fixture", "fixture");

    private static UiAutomationElementTarget ElementTarget() => new(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "0x123",
        42,
        123456,
        "Fixture",
        "fixture",
        "42.1.2",
        "입력",
        "InputBox",
        "Edit",
        false,
        true,
        true,
        false);

    private static ToolInvokeParams InspectInvocation() => new(
        "inspect", "conversation", "run", "uia.inspect.v1", "R1",
        new Dictionary<string, object?>
        {
            ["windowId"] = "0x123",
            ["reason"] = "화면 요소를 찾기 위해",
        });

    private static ToolInvokeParams ActionInvocation(
        string name,
        string risk,
        string? payloadKey = null,
        string? payload = null)
    {
        var input = new Dictionary<string, object?>
        {
            ["elementId"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["reason"] = "요청한 화면 동작을 수행하기 위해",
        };
        if (payloadKey is not null) input[payloadKey] = payload;
        return new ToolInvokeParams("action", "conversation", "run", name, risk, input);
    }

    private sealed class FakeUiAutomationService : IUiAutomationService
    {
        public UiAutomationWindowTarget? Window { get; set; }
        public UiAutomationInspection? Inspection { get; set; }
        public UiAutomationElementTarget Target { get; set; } = ElementTarget();
        public int MaximumElements { get; private set; }
        public int InspectCount { get; private set; }
        public int InvokeCount { get; private set; }
        public string? SetValue { get; private set; }
        public string? SentText { get; private set; }
        public UiAutomationActionStatus SendStatus { get; set; } = UiAutomationActionStatus.Succeeded;

        public Task<UiAutomationWindowTarget?> ResolveWindowAsync(string windowId, CancellationToken cancellationToken) =>
            Task.FromResult(Window);

        public Task<UiAutomationInspection?> InspectAsync(
            string windowId,
            string? query,
            int maximumElements,
            CancellationToken cancellationToken)
        {
            InspectCount++;
            MaximumElements = maximumElements;
            return Task.FromResult(Inspection);
        }

        public Task<UiAutomationElementTarget?> ResolveAsync(string elementId, CancellationToken cancellationToken) =>
            Task.FromResult<UiAutomationElementTarget?>(Target);

        public Task<UiAutomationActionStatus> InvokeAsync(
            UiAutomationElementTarget expected,
            CancellationToken cancellationToken)
        {
            InvokeCount++;
            return Task.FromResult(UiAutomationActionStatus.Succeeded);
        }

        public Task<UiAutomationActionStatus> SetValueAsync(
            UiAutomationElementTarget expected,
            string value,
            CancellationToken cancellationToken)
        {
            SetValue = value;
            return Task.FromResult(UiAutomationActionStatus.Succeeded);
        }

        public Task<UiAutomationActionStatus> SendTextAsync(
            UiAutomationElementTarget expected,
            string text,
            CancellationToken cancellationToken)
        {
            if (SendStatus == UiAutomationActionStatus.Succeeded) SentText = text;
            return Task.FromResult(SendStatus);
        }
    }

    private sealed class FakeAuditSink : IToolAuditSink
    {
        public List<ToolApprovalAuditRecord> Approvals { get; } = [];
        public List<ToolExecutionAuditRecord> Executions { get; } = [];
        public Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken)
        {
            Approvals.Add(record);
            return Task.CompletedTask;
        }
        public Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken)
        {
            Executions.Add(record);
            return Task.CompletedTask;
        }
    }
}
