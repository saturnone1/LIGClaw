using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

public enum WorkflowRunStatus
{
    Completed,
    NeedsInformation,
    Failed,
    Cancelled,
    AttentionRequired,
}

public enum WorkflowNextAction
{
    None,
    ProvideInformation,
    RetryFromStart,
    ReviewOutcome,
}

public enum WorkflowToolExecutionDisposition
{
    Succeeded,
    RetryableFailed,
    FailedKnown,
    NotStarted,
    Cancelled,
    OutcomeUnknown,
}

public sealed record WorkflowRunIdentity(
    string WorkflowRunId,
    string ConversationId,
    string ParentRunId,
    string ParentToolCallId,
    CapabilityVersionRef ExactCapabilityVersion,
    long CatalogRevision);

public sealed record WorkflowToolExecutionRequest(
    WorkflowRunIdentity Run,
    WorkflowDefinitionHash DefinitionHash,
    WorkflowPlanHash PlanHash,
    string InputHash,
    string AuthorizationHash,
    int StepOrdinal,
    string StepId,
    int Attempt,
    int MaximumRetries,
    string ToolName,
    string Risk,
    WorkflowToolPreparationMode PreparationMode,
    string InputContractHash,
    string ResultContractHash,
    WorkflowObjectValue Payload);

public sealed record WorkflowToolExecutionResponse(
    WorkflowToolExecutionDisposition Disposition,
    WorkflowObjectValue? Output = null,
    string? ErrorCode = null,
    string? SummaryCode = null);

public interface IWorkflowToolExecutor
{
    Task<WorkflowToolExecutionResponse> ExecuteAsync(
        WorkflowToolExecutionRequest request,
        CancellationToken cancellationToken);
}

public sealed record WorkflowStepTrace(
    int StepOrdinal,
    string StepId,
    int Attempt,
    string Status,
    string? ErrorCode = null);

public sealed record WorkflowRunResult
{
    private readonly IReadOnlyList<string> requiredInputs;
    private readonly IReadOnlyList<WorkflowStepTrace> trace;

    public WorkflowRunResult(
        WorkflowRunStatus status,
        WorkflowNextAction nextAction,
        string inputHash,
        string authorizationHash,
        WorkflowObjectValue? output,
        IReadOnlyList<string>? requiredInputs,
        IReadOnlyList<WorkflowStepTrace> trace,
        string? errorCode = null)
    {
        Status = status;
        NextAction = nextAction;
        InputHash = inputHash;
        AuthorizationHash = authorizationHash;
        Output = output;
        this.requiredInputs = Array.AsReadOnly((requiredInputs ?? []).ToArray());
        this.trace = Array.AsReadOnly(trace.ToArray());
        ErrorCode = errorCode;
    }

    public WorkflowRunStatus Status { get; }
    public WorkflowNextAction NextAction { get; }
    public string InputHash { get; }
    public string AuthorizationHash { get; }
    public WorkflowObjectValue? Output { get; }
    public IReadOnlyList<string> RequiredInputs => requiredInputs;
    public IReadOnlyList<WorkflowStepTrace> Trace => trace;
    public string? ErrorCode { get; }
}

internal static class WorkflowRuntimeValue
{
    private const int MaximumCanonicalBytes = 1_048_576;
    private const int WriterSlackBytes = 4_096;
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    public static bool TryCanonicalize(WorkflowValue value, int maximumBytes, out byte[] canonical)
    {
        canonical = [];
        if (maximumBytes is < 2 or > MaximumCanonicalBytes ||
            !HasBoundedShape(value, maximumBytes, 0, new Counter()))
            return false;
        try
        {
            var writerCapacity = checked(maximumBytes * 6 + WriterSlackBytes);
            var buffer = new FixedCapacityBufferWriter(writerCapacity);
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                Write(writer, value);
            }
            if (buffer.WrittenCount > maximumBytes) return false;
            canonical = buffer.WrittenSpan.ToArray();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                          EncoderFallbackException or JsonException or OverflowException)
        {
            return false;
        }
    }

    public static string Hash(ReadOnlySpan<byte> canonical) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()}";

    public static bool TryResolvePointer(
        WorkflowValue root,
        string pointer,
        out WorkflowValue? value)
    {
        value = root;
        if (pointer.Length == 0) return true;
        if (!TryDecodePointer(pointer, out var tokens)) return false;
        foreach (var token in tokens)
        {
            switch (value)
            {
                case WorkflowObjectValue valueObject when valueObject.Properties.TryGetValue(token, out var property):
                    value = property;
                    break;
                case WorkflowArrayValue array when IsCanonicalArrayIndex(token) &&
                                                   int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                                                   index < array.Items.Count:
                    value = array.Items[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }
        return true;
    }

    public static bool StructuralEquals(WorkflowValue left, WorkflowValue right) => (left, right) switch
    {
        (WorkflowNullValue, WorkflowNullValue) => true,
        (WorkflowBooleanValue leftBoolean, WorkflowBooleanValue rightBoolean) =>
            leftBoolean.Value == rightBoolean.Value,
        (WorkflowNumberValue leftNumber, WorkflowNumberValue rightNumber) =>
            leftNumber.IsInteger == rightNumber.IsInteger &&
            StringComparer.Ordinal.Equals(leftNumber.CanonicalValue, rightNumber.CanonicalValue),
        (WorkflowStringValue leftText, WorkflowStringValue rightText) =>
            StringComparer.Ordinal.Equals(leftText.Value, rightText.Value),
        (WorkflowArrayValue leftArray, WorkflowArrayValue rightArray) =>
            leftArray.Items.Count == rightArray.Items.Count &&
            leftArray.Items.Zip(rightArray.Items).All(pair => StructuralEquals(pair.First, pair.Second)),
        (WorkflowObjectValue leftObject, WorkflowObjectValue rightObject) =>
            leftObject.Properties.Count == rightObject.Properties.Count &&
            leftObject.Properties.All(pair =>
                rightObject.Properties.TryGetValue(pair.Key, out var rightValue) &&
                StructuralEquals(pair.Value, rightValue)),
        _ => false,
    };

    public static string ComputeAuthorizationHash(
        WorkflowRunIdentity run,
        ValidatedWorkflowPlan plan,
        string inputHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", "workflow-authorization-v1");
        Append(hash, "workflowRunId", run.WorkflowRunId);
        Append(hash, "conversationId", run.ConversationId);
        Append(hash, "parentRunId", run.ParentRunId);
        Append(hash, "parentToolCallId", run.ParentToolCallId);
        Append(hash, "capabilityId", run.ExactCapabilityVersion.Id.Value);
        Append(hash, "capabilityVersion", run.ExactCapabilityVersion.Version.Value);
        Append(hash, "capabilityContentHash", run.ExactCapabilityVersion.ContentHash.Value);
        Append(hash, "catalogRevision", run.CatalogRevision.ToString(CultureInfo.InvariantCulture));
        Append(hash, "definitionHash", plan.DefinitionHash.Value);
        Append(hash, "inputContractHash", plan.InputContractHash);
        Append(hash, "planHash", plan.PlanHash.Value);
        Append(hash, "inputHash", inputHash);
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static bool HasBoundedShape(WorkflowValue value, int maximumBytes, int depth, Counter counter)
    {
        if (depth > 16 || ++counter.Nodes > 4_096) return false;
        switch (value)
        {
            case WorkflowNullValue or WorkflowBooleanValue:
                return true;
            case WorkflowNumberValue number when number.CanonicalValue is not null &&
                                                  number.CanonicalValue.Length <= 64 &&
                                                  WorkflowDefinitionParser.TryCanonicalizeRuntimeNumber(
                                                      number.CanonicalValue,
                                                      out var canonicalNumber) && canonicalNumber is not null:
                return canonicalNumber.IsInteger == number.IsInteger &&
                       StringComparer.Ordinal.Equals(canonicalNumber.CanonicalValue, number.CanonicalValue);
            case WorkflowStringValue text:
                return text.Value is not null && text.Value.Length <= maximumBytes;
            case WorkflowArrayValue array:
                return array.Items.Count <= 128 &&
                       array.Items.All(item => HasBoundedShape(item, maximumBytes, depth + 1, counter));
            case WorkflowObjectValue valueObject:
                return valueObject.Properties.Count <= 128 && valueObject.Properties.All(pair =>
                    pair.Key is not null && pair.Key.Length is >= 1 and <= 80 && pair.Value is not null &&
                    HasBoundedShape(pair.Value, maximumBytes, depth + 1, counter));
            default:
                return false;
        }
    }

    private static void Write(Utf8JsonWriter writer, WorkflowValue value)
    {
        switch (value)
        {
            case WorkflowNullValue:
                writer.WriteNullValue();
                break;
            case WorkflowBooleanValue boolean:
                writer.WriteBooleanValue(boolean.Value);
                break;
            case WorkflowNumberValue number:
                writer.WriteRawValue(number.CanonicalValue, skipInputValidation: false);
                break;
            case WorkflowStringValue text:
                writer.WriteStringValue(text.Value);
                break;
            case WorkflowArrayValue array:
                writer.WriteStartArray();
                foreach (var item in array.Items) Write(writer, item);
                writer.WriteEndArray();
                break;
            case WorkflowObjectValue valueObject:
                writer.WriteStartObject();
                foreach (var pair in valueObject.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException("평가되지 않은 Workflow value는 직렬화할 수 없습니다.");
        }
    }

    private static void Append(IncrementalHash hash, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> framing = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(framing, nameBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(framing[4..], valueBytes.LongLength);
        hash.AppendData(framing);
        hash.AppendData(nameBytes);
        hash.AppendData(valueBytes);
    }

    private static bool IsCanonicalArrayIndex(string token) =>
        token == "0" || token.Length > 0 && token[0] is >= '1' and <= '9' && token.All(char.IsAsciiDigit);

    private static bool TryDecodePointer(string pointer, out IReadOnlyList<string> tokens)
    {
        tokens = [];
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;
        var result = new List<string>();
        foreach (var encoded in pointer[1..].Split('/'))
        {
            var decoded = new StringBuilder(encoded.Length);
            for (var index = 0; index < encoded.Length; index++)
            {
                if (encoded[index] != '~')
                {
                    decoded.Append(encoded[index]);
                    continue;
                }
                if (++index >= encoded.Length || encoded[index] is not ('0' or '1')) return false;
                decoded.Append(encoded[index] == '0' ? '~' : '/');
            }
            result.Add(decoded.ToString());
        }
        tokens = result;
        return true;
    }

    private sealed class Counter
    {
        public int Nodes { get; set; }
    }

    private sealed class FixedCapacityBufferWriter(int capacity) : IBufferWriter<byte>
    {
        private readonly byte[] buffer = new byte[capacity];
        private int written;

        public int WrittenCount => written;
        public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

        public void Advance(int count)
        {
            if (count < 0 || count > buffer.Length - written)
                throw new InvalidOperationException("Workflow canonical JSON exceeded its fixed buffer.");
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var requested = sizeHint <= 0 ? 1 : sizeHint;
            if (requested > buffer.Length - written)
                throw new InvalidOperationException("Workflow canonical JSON exceeded its fixed buffer.");
            return buffer.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
