using System.Security.Cryptography;
using System.Text;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

public static class WorkflowRuntimeContractValidator
{
    public static bool ValidateAuthorizationHash(
        WorkflowRunIdentity run,
        ValidatedWorkflowPlan plan,
        string inputHash,
        string authorizationHash)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputHash);
        ArgumentNullException.ThrowIfNull(authorizationHash);
        try
        {
            return StringComparer.Ordinal.Equals(
                WorkflowRuntimeValue.ComputeAuthorizationHash(run, plan, inputHash),
                authorizationHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          FormatException or OverflowException)
        {
            return false;
        }
    }

    public static bool TryComputeCanonicalHash(
        WorkflowValue value,
        int maximumBytes,
        out string hash)
    {
        ArgumentNullException.ThrowIfNull(value);
        hash = string.Empty;
        if (!WorkflowRuntimeValue.TryCanonicalize(value, maximumBytes, out var canonical)) return false;
        hash = WorkflowRuntimeValue.Hash(canonical);
        return true;
    }

    public static bool ValidateObject(
        string canonicalSchemaJson,
        string expectedContractHash,
        WorkflowObjectValue value)
    {
        ArgumentNullException.ThrowIfNull(canonicalSchemaJson);
        ArgumentNullException.ThrowIfNull(expectedContractHash);
        ArgumentNullException.ThrowIfNull(value);
        var actualHash =
            $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchemaJson))).ToLowerInvariant()}";
        return StringComparer.Ordinal.Equals(actualHash, expectedContractHash) &&
               WorkflowContractSchema.TryParse(canonicalSchemaJson, out var schema) &&
               schema is not null && schema.Type == "object" && schema.AcceptsValue(value, _ => null);
    }
}
