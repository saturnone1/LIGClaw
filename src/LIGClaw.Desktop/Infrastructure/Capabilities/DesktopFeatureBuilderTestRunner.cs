using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Workflows;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed class DesktopFeatureBuilderTestRunner(
    IFeatureBuilderManagementService management,
    IFeatureBuilderRepository repository,
    IFeatureBuilderProposalScanner scanner,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async Task<FeatureBuilderManagementResult> RunRequiredTestsAsync(
        FeatureBuilderProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.State != FeatureBuilderProposalState.Pending ||
            proposal.Candidate is null || proposal.CandidateContentHash is null)
        {
            return new FeatureBuilderManagementResult(
                FeatureBuilderManagementOutcome.NotReady,
                Proposal: proposal,
                ErrorCode: "feature_builder.proposal.not_pending");
        }

        var staticResult = await RunOneAsync(
            proposal,
            FeatureBuilderTestRunKind.StaticScan,
            "static-scan-v1",
            _ => Task.FromResult(RunStaticScan(proposal)),
            cancellationToken).ConfigureAwait(false);
        if (staticResult.Outcome != FeatureBuilderManagementOutcome.Succeeded ||
            staticResult.TestRun?.State != FeatureBuilderTestRunState.Passed)
            return staticResult;

        var fixtureValidation = CapabilityFixtureTestPolicy.Validate(proposal.Candidate.CanonicalTestsJson);
        var engineVersion = fixtureValidation.WorkflowSemanticCases.Count > 0
            ? "workflow-semantic-v2"
            : "fixture-dry-run-v1";
        return await RunOneAsync(
            proposal,
            FeatureBuilderTestRunKind.DryRun,
            engineVersion,
            token => RunFixtureDryRunAsync(proposal.Candidate, fixtureValidation, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FeatureBuilderManagementResult> RunOneAsync(
        FeatureBuilderProposal proposal,
        FeatureBuilderTestRunKind kind,
        string engineVersion,
        Func<CancellationToken, Task<TestEvaluation>> evaluate,
        CancellationToken cancellationToken)
    {
        var existing = await repository.ListTestRunsAsync(
            proposal.ProposalId, cancellationToken).ConfigureAwait(false);
        var attempt = existing.Where(run => run.Kind == kind)
            .Select(run => run.Attempt)
            .DefaultIfEmpty(0)
            .Max() + 1;
        if (attempt > 16)
        {
            return new FeatureBuilderManagementResult(
                FeatureBuilderManagementOutcome.NotReady,
                Proposal: proposal,
                ErrorCode: "feature_builder.test.attempts_exhausted");
        }

        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var started = await management.StartTestRunAsync(
            new StartFeatureBuilderTestRunCommand(
                NewTestRunId(),
                proposal.ProposalId,
                kind,
                attempt,
                engineVersion,
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        if (started.Outcome != FeatureBuilderManagementOutcome.Succeeded || started.TestRun is null)
            return started;

        TestEvaluation evaluation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluation = await evaluate(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await management.CompleteTestRunAsync(
                new CompleteFeatureBuilderTestRunCommand(
                    started.TestRun.TestRunId,
                    started.TestRun.Revision,
                    FeatureBuilderTestRunState.Interrupted,
                    "{\"passed\":false}",
                    "feature_builder.test.interrupted",
                    _timeProvider.GetUtcNow().ToUniversalTime()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            evaluation = new TestEvaluation(
                false,
                "{\"passed\":false}",
                "feature_builder.test.failed");
        }

        return await management.CompleteTestRunAsync(
            new CompleteFeatureBuilderTestRunCommand(
                started.TestRun.TestRunId,
                started.TestRun.Revision,
                evaluation.Passed ? FeatureBuilderTestRunState.Passed : FeatureBuilderTestRunState.Failed,
                evaluation.CanonicalResultJson,
                evaluation.Passed ? null : evaluation.ErrorCode ?? "feature_builder.test.failed",
                _timeProvider.GetUtcNow().ToUniversalTime()),
            cancellationToken).ConfigureAwait(false);
    }

    private TestEvaluation RunStaticScan(FeatureBuilderProposal proposal)
    {
        var candidate = proposal.Candidate!;
        var artifacts = ToArtifacts(candidate);
        var revisionTarget = proposal.BaseVersion is null
            ? null
            : new FeatureBuilderRevisionTarget(proposal.BaseVersion, proposal.ExpectedCatalogRevision);
        var result = scanner.Scan(artifacts, revisionTarget);
        var passed = result.IsValid && result.CandidateContentHash is not null &&
                     result.CandidateContentHash.Equals(proposal.CandidateContentHash);
        return new TestEvaluation(
            passed,
            JsonSerializer.Serialize(new
            {
                passed,
                checkCount = 6,
                failedCheckCount = passed ? 0 : 1,
            }),
            passed ? null : result.ErrorCode ?? "feature_builder.static_scan.failed");
    }

    private async Task<TestEvaluation> RunFixtureDryRunAsync(
        CapabilityVersionPayload candidate,
        CapabilityFixtureTestValidation validation,
        CancellationToken cancellationToken)
    {
        if (!validation.IsValid || validation.CaseCount == 0)
        {
            return new TestEvaluation(
                false,
                "{\"passed\":false,\"caseCount\":0,\"matchedCount\":0}",
                validation.ErrorCode ?? "feature_builder.proposal.tests_required");
        }

        if (validation.WorkflowSemanticCases.Count > 0)
        {
            var preparationError = "feature_builder.semantic.workflow_required";
            if (scanner is not DesktopFeatureBuilderProposalScanner desktopScanner ||
                !desktopScanner.TryPrepareNoEffectWorkflowSimulation(
                    candidate,
                    out var plan,
                    out var toolCatalog,
                    out preparationError) ||
                plan is null || toolCatalog is null)
            {
                return new TestEvaluation(
                    false,
                    JsonSerializer.Serialize(new
                    {
                        passed = false,
                        schemaVersion = 2,
                        caseCount = validation.WorkflowSemanticCases.Count,
                        matchedCount = 0,
                    }),
                    preparationError);
            }
            var semantic = await new FeatureBuilderWorkflowSemanticSimulator(toolCatalog).RunAsync(
                candidate,
                plan,
                validation.WorkflowSemanticCases,
                cancellationToken).ConfigureAwait(false);
            return new TestEvaluation(
                semantic.Passed,
                JsonSerializer.Serialize(new
                {
                    passed = semantic.Passed,
                    schemaVersion = 2,
                    caseCount = semantic.CaseCount,
                    matchedCount = semantic.MatchedCount,
                    cases = semantic.Cases.Select(result => new
                    {
                        caseHash = result.CaseHash,
                        passed = result.Passed,
                    }),
                }),
                semantic.ErrorCode);
        }

        var schemaHash = $"sha256:{Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate.CanonicalInputSchemaJson)))}";
        var matched = 0;
        foreach (var testCase in validation.Cases)
        {
            using var document = JsonDocument.Parse(testCase.CanonicalInputJson);
            var input = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
            var accepted = WorkflowToolValueConverter.TryFromToolOutput(input, out var workflowValue) &&
                           workflowValue is not null &&
                           WorkflowRuntimeContractValidator.ValidateObject(
                               candidate.CanonicalInputSchemaJson,
                               schemaHash,
                               workflowValue);
            if (accepted == StringComparer.Ordinal.Equals(testCase.Expected, "accepted")) matched++;
        }

        var passed = matched == validation.Cases.Count;
        return new TestEvaluation(
            passed,
            JsonSerializer.Serialize(new
            {
                passed,
                caseCount = validation.Cases.Count,
                matchedCount = matched,
            }),
            passed ? null : "feature_builder.dry_run.fixture_mismatch");
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> ToArtifacts(
        CapabilityVersionPayload payload)
    {
        var artifacts = new List<FeatureBuilderRawProposalArtifact>
        {
            new("manifest.json", "application/json", payload.CanonicalManifestJson),
            new("instructions.md", "text/markdown", payload.Instructions),
            new("input.schema.json", "application/json", payload.CanonicalInputSchemaJson),
            new("tests.json", "application/json", payload.CanonicalTestsJson),
        };
        if (payload.CanonicalSettingsSchemaJson is not null)
            artifacts.Add(new("settings.schema.json", "application/json", payload.CanonicalSettingsSchemaJson));
        if (payload.CanonicalWorkflowJson is not null)
            artifacts.Add(new("workflow.json", "application/json", payload.CanonicalWorkflowJson));
        return artifacts.AsReadOnly();
    }

    private static string NewTestRunId() =>
        $"fbtest_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";

    private sealed record TestEvaluation(
        bool Passed,
        string CanonicalResultJson,
        string? ErrorCode);
}
