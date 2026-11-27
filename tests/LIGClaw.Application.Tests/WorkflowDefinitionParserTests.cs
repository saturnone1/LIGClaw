using LIGClaw.Application.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class WorkflowDefinitionParserTests
{
    private readonly WorkflowDefinitionParser _parser = new();

    [Fact]
    public void ParsesAllSixStepKindsAndProducesStableCanonicalJson()
    {
        const string json = """
            {
              "steps": [
                {
                  "next": "check",
                  "saveAs": "mapped",
                  "value": { "$bind": { "path": "/path", "source": "input" } },
                  "type": "input.map",
                  "id": "map"
                },
                {
                  "else": "ask",
                  "then": "normalize",
                  "operator": "exists",
                  "left": { "$bind": { "source": "variable", "name": "mapped", "path": "" } },
                  "type": "condition",
                  "id": "check"
                },
                {
                  "next": "status",
                  "saveAs": "normalized",
                  "operands": [{ "$bind": { "source": "variable", "name": "mapped", "path": "" } }],
                  "operation": "trim",
                  "type": "transform",
                  "id": "normalize"
                },
                {
                  "requiredInputs": ["/path"],
                  "type": "needs_information",
                  "id": "ask"
                },
                {
                  "next": "done",
                  "arguments": {},
                  "tool": "system.get_status.v1",
                  "type": "tool.call",
                  "id": "status"
                },
                {
                  "value": { "$bind": { "source": "step", "stepId": "status", "path": "/windowsRelease" } },
                  "type": "output",
                  "id": "done"
                }
              ],
              "entryStepId": "map",
              "schemaVersion": 1
            }
            """;

        var result = _parser.Parse(json);

        Assert.True(result.Succeeded, result.Issues.FirstOrDefault()?.Code);
        Assert.NotNull(result.Definition);
        Assert.Collection(
            result.Definition.Steps,
            step => Assert.IsType<WorkflowInputMapStep>(step),
            step => Assert.IsType<WorkflowConditionStep>(step),
            step => Assert.IsType<WorkflowTransformStep>(step),
            step => Assert.IsType<WorkflowNeedsInformationStep>(step),
            step => Assert.IsType<WorkflowToolCallStep>(step),
            step => Assert.IsType<WorkflowOutputStep>(step));
        Assert.NotNull(result.CanonicalJson);
        var roundTrip = _parser.Parse(result.CanonicalJson);
        Assert.True(roundTrip.Succeeded);
        Assert.Equal(result.CanonicalJson, roundTrip.CanonicalJson);
    }

    [Theory]
    [InlineData(
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":null}]}",
        "workflow.object.duplicate_property")]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"call\",\"steps\":[{\"id\":\"call\",\"type\":\"tool.call\",\"tool\":\"system.get_status.v1\",\"arguments\":{\"Path\":1,\"path\":2},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":null}]}",
        "workflow.object.case_collision")]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":null,\"secretField\":\"must-not-echo\"}]}",
        "workflow.object.unknown_property")]
    public void RejectsDuplicateCaseCollidingAndUnknownPropertiesWithoutEchoingValues(
        string json,
        string expectedCode)
    {
        var result = _parser.Parse(json);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(expectedCode, issue.Code);
        Assert.DoesNotContain("must-not-echo", issue.Path, StringComparison.Ordinal);
        Assert.Null(result.Definition);
        Assert.Null(result.CanonicalJson);
    }

    [Theory]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":{\"$bind\":{\"source\":\"input\",\"path\":\"/bad~2pointer\"}}}]}",
        "workflow.pointer.invalid")]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":9007199254740992}]}",
        "workflow.template.number_invalid")]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":\"hidden\\u200btext\"}]}",
        "workflow.template.string_invalid")]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":\"hidden\\udb40\\udc01text\"}]}",
        "workflow.template.string_invalid")]
    [InlineData(
        "{\"schemaVersion\":1,\"entryStepId\":\"check\",\"steps\":[{\"id\":\"check\",\"type\":\"condition\",\"left\":true,\"operator\":\"equals\",\"then\":\"done\",\"else\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":null}]}",
        "workflow.condition.right_invalid")]
    public void RejectsUnsafePointersNumbersTextAndVariantShapes(string json, string expectedCode)
    {
        var issue = Assert.Single(_parser.Parse(json).Issues);

        Assert.Equal(expectedCode, issue.Code);
    }

    [Fact]
    public void RejectsDuplicateStepIdsAndMoreThanTwentySteps()
    {
        const string collision = """
            {
              "schemaVersion": 1,
              "entryStepId": "first",
              "steps": [
                { "id": "first", "type": "input.map", "value": null, "saveAs": "one", "next": "first" },
                { "id": "first", "type": "output", "value": null }
              ]
            }
            """;
        var steps = string.Join(
            ',',
            Enumerable.Range(0, 21).Select(index =>
                index == 20
                    ? $"{{\"id\":\"s{index}\",\"type\":\"output\",\"value\":null}}"
                    : $"{{\"id\":\"s{index}\",\"type\":\"input.map\",\"value\":null,\"saveAs\":\"v{index}\",\"next\":\"s{index + 1}\"}}"));
        var oversized = $"{{\"schemaVersion\":1,\"entryStepId\":\"s0\",\"steps\":[{steps}]}}";

        Assert.Equal("workflow.step.id_collision", Assert.Single(_parser.Parse(collision).Issues).Code);
        Assert.Equal("workflow.step.count_out_of_bounds", Assert.Single(_parser.Parse(oversized).Issues).Code);
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("1e0", "1")]
    [InlineData("-0.0", "0")]
    public void IntegralNumberLexemesHaveStableCanonicalAstAndDefinitionHash(
        string number,
        string expectedCanonicalValue)
    {
        var json =
            $"{{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{{\"id\":\"done\",\"type\":\"output\",\"value\":{number}}}]}}";

        var first = _parser.Parse(json);

        Assert.True(first.Succeeded, first.Issues.FirstOrDefault()?.Code);
        var firstOutput = Assert.IsType<WorkflowOutputStep>(Assert.Single(first.Definition!.Steps));
        var firstNumber = Assert.IsType<WorkflowNumberValue>(firstOutput.Value);
        Assert.True(firstNumber.IsInteger);
        Assert.Equal(expectedCanonicalValue, firstNumber.CanonicalValue);

        var second = _parser.Parse(first.CanonicalJson!);

        Assert.True(second.Succeeded, second.Issues.FirstOrDefault()?.Code);
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(
            WorkflowPlanHasher.ComputeDefinitionHash(first.Definition),
            WorkflowPlanHasher.ComputeDefinitionHash(second.Definition!));
    }

    [Theory]
    [InlineData("9007199254740992.0")]
    [InlineData("9.007199254740992e15")]
    public void RejectsUnsafeIntegralNumbersAcrossDecimalAndExponentLexemes(string number)
    {
        var json =
            $"{{\"schemaVersion\":1,\"entryStepId\":\"done\",\"steps\":[{{\"id\":\"done\",\"type\":\"output\",\"value\":{number}}}]}}";

        var issue = Assert.Single(_parser.Parse(json).Issues);

        Assert.Equal("workflow.template.number_invalid", issue.Code);
    }

    [Fact]
    public void DefinitionDeepCopiesCallerOwnedStepList()
    {
        var source = new List<WorkflowStep>
        {
            new WorkflowOutputStep("done", WorkflowNullValue.Instance),
        };
        var definition = new WorkflowDefinition(1, "done", source);

        source.Clear();

        Assert.Single(definition.Steps);
    }
}
