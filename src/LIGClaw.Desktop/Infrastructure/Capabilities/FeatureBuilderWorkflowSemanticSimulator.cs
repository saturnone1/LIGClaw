using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Workflows;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed record FeatureBuilderSemanticCaseResult(string CaseHash, bool Passed);

internal sealed record FeatureBuilderSemanticSimulationResult(
    bool Passed,
    int CaseCount,
    int MatchedCount,
    IReadOnlyList<FeatureBuilderSemanticCaseResult> Cases,
    string? ErrorCode = null);

internal sealed class FeatureBuilderWorkflowSemanticSimulator(IWorkflowToolMetadataCatalog toolCatalog)
{
    internal async Task<FeatureBuilderSemanticSimulationResult> RunAsync(
        CapabilityVersionPayload candidate,
        ValidatedWorkflowPlan plan,
        IReadOnlyList<CapabilityWorkflowSemanticTestCase> testCases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(testCases);
        if (candidate.Manifest.Kind != CapabilityKind.Workflow || testCases.Count == 0 ||
            plan.Steps.Any(step => step.CanonicalToolName is not null &&
                (step.EffectClass != WorkflowToolEffectClass.Observation ||
                 step.PreparationMode != WorkflowToolPreparationMode.None)))
            return Failed(testCases, "feature_builder.semantic.effect_forbidden");

        var results = new List<FeatureBuilderSemanticCaseResult>(testCases.Count);
        var matched = 0;
        for (var index = 0; index < testCases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var testCase = testCases[index];
            var passed = await RunCaseAsync(candidate, plan, testCase, index, cancellationToken)
                .ConfigureAwait(false);
            if (passed) matched++;
            results.Add(new FeatureBuilderSemanticCaseResult(HashCaseName(testCase.Name), passed));
        }
        return new FeatureBuilderSemanticSimulationResult(
            matched == testCases.Count,
            testCases.Count,
            matched,
            results.AsReadOnly(),
            matched == testCases.Count ? null : "feature_builder.semantic.fixture_mismatch");
    }

    private async Task<bool> RunCaseAsync(
        CapabilityVersionPayload candidate,
        ValidatedWorkflowPlan plan,
        CapabilityWorkflowSemanticTestCase testCase,
        int caseIndex,
        CancellationToken cancellationToken)
    {
        if (!TryReadObject(testCase.CanonicalInputJson, out var input) || input is null) return false;
        var executor = new DeterministicNoEffectToolExecutor(toolCatalog, testCase.ToolResults);
        var identity = new WorkflowRunIdentity(
            $"builder-semantic-{caseIndex}",
            "builder-semantic-conversation",
            "builder-semantic-parent-run",
            $"builder-semantic-tool-call-{caseIndex}",
            new CapabilityVersionRef(
                candidate.Manifest.Id,
                candidate.Manifest.Version,
                CapabilityContentHasher.Compute(candidate)),
            1);
        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            candidate.CanonicalInputSchemaJson,
            input,
            identity,
            executor,
            cancellationToken).ConfigureAwait(false);

        if (!executor.AllExpectedResultsConsumed ||
            !StringComparer.Ordinal.Equals(FormatStatus(result.Status), testCase.Expected.Status) ||
            !result.Trace.Select(trace => trace.StepId).SequenceEqual(
                testCase.Expected.VisitedSteps,
                StringComparer.Ordinal))
            return false;
        if (testCase.Expected.CanonicalOutputJson is null) return result.Output is null;
        if (result.Output is null ||
            !TryReadObject(testCase.Expected.CanonicalOutputJson, out var expectedOutput) ||
            expectedOutput is null ||
            !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                result.Output,
                WorkflowInterpreter.MaximumFinalOutputBytes,
                out var actualOutputHash) ||
            !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                expectedOutput,
                WorkflowInterpreter.MaximumFinalOutputBytes,
                out var expectedOutputHash))
            return false;
        return StringComparer.Ordinal.Equals(actualOutputHash, expectedOutputHash);
    }

    private static string FormatStatus(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Completed => "completed",
        WorkflowRunStatus.NeedsInformation => "needs_information",
        WorkflowRunStatus.Failed => "failed",
        WorkflowRunStatus.Cancelled => "cancelled",
        WorkflowRunStatus.AttentionRequired => "attention_required",
        _ => string.Empty,
    };

    private static bool TryReadObject(string canonicalJson, out WorkflowObjectValue? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var dictionary = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
            return WorkflowToolValueConverter.TryFromToolOutput(dictionary, out value) && value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string HashCaseName(string name) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))}";

    private static FeatureBuilderSemanticSimulationResult Failed(
        IReadOnlyList<CapabilityWorkflowSemanticTestCase> testCases,
        string errorCode) =>
        new(
            false,
            testCases.Count,
            0,
            testCases.Select(testCase =>
                    new FeatureBuilderSemanticCaseResult(HashCaseName(testCase.Name), false))
                .ToArray(),
            errorCode);

    private sealed class DeterministicNoEffectToolExecutor(
        IWorkflowToolMetadataCatalog catalog,
        IReadOnlyList<CapabilityWorkflowSemanticToolResult> expectedResults) : IWorkflowToolExecutor
    {
        private int nextResult;
        private bool mismatch;

        internal bool AllExpectedResultsConsumed => !mismatch && nextResult == expectedResults.Count;

        public Task<WorkflowToolExecutionResponse> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.PreparationMode != WorkflowToolPreparationMode.None ||
                nextResult >= expectedResults.Count ||
                !catalog.TryGet(request.ToolName, out var metadata) || metadata is null ||
                metadata.EffectClass != WorkflowToolEffectClass.Observation ||
                metadata.PreparationMode != WorkflowToolPreparationMode.None ||
                !StringComparer.Ordinal.Equals(metadata.CanonicalName, request.ToolName) ||
                !WorkflowRuntimeContractValidator.ValidateObject(
                    metadata.CanonicalInputSchemaJson,
                    request.InputContractHash,
                    request.Payload) ||
                !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                    request.Payload,
                    WorkflowInterpreter.MaximumInputBytes,
                    out var actualArgumentsHash))
                return Mismatch();

            var expected = expectedResults[nextResult];
            if (!StringComparer.Ordinal.Equals(expected.StepId, request.StepId) ||
                !StringComparer.Ordinal.Equals(expected.ToolName, request.ToolName) ||
                !TryReadObject(expected.CanonicalArgumentsJson, out var expectedArguments) ||
                expectedArguments is null ||
                !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                    expectedArguments,
                    WorkflowInterpreter.MaximumInputBytes,
                    out var expectedArgumentsHash) ||
                !StringComparer.Ordinal.Equals(expectedArgumentsHash, actualArgumentsHash) ||
                !TryReadObject(expected.CanonicalOutputJson, out var output) || output is null ||
                !WorkflowRuntimeContractValidator.ValidateObject(
                    metadata.CanonicalResultSchemaJson,
                    request.ResultContractHash,
                    output))
                return Mismatch();

            nextResult++;
            return Task.FromResult(new WorkflowToolExecutionResponse(
                WorkflowToolExecutionDisposition.Succeeded,
                output));
        }

        private Task<WorkflowToolExecutionResponse> Mismatch()
        {
            mismatch = true;
            return Task.FromResult(new WorkflowToolExecutionResponse(
                WorkflowToolExecutionDisposition.NotStarted,
                ErrorCode: "feature_builder.semantic.tool_mismatch"));
        }
    }
}
