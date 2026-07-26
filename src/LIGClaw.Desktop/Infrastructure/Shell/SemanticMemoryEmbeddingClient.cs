using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record SemanticMemorySettings(
    bool Enabled,
    string BaseUrl,
    string Model,
    string? ApiKey = null);

internal static class SemanticMemorySettingsPolicy
{
    internal const int MaximumInputCharacters = 16_000;

    internal static bool TryValidate(
        SemanticMemorySettings settings,
        out SemanticMemorySettings normalized,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        var model = settings.Model.Trim();
        normalized = settings with { BaseUrl = baseUrl, Model = model };
        error = string.Empty;
        if (!settings.Enabled) return true;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "Embedding 주소를 HTTP 또는 HTTPS로 입력해 주세요.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Embedding 주소에는 사용자 정보나 # 조각을 넣을 수 없습니다.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200)
        {
            error = "Embedding 모델 이름을 입력해 주세요.";
            return false;
        }
        return true;
    }

    internal static Uri ResolveEndpoint(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        if (normalized.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)) return new Uri(normalized);
        return new Uri(normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{normalized}/embeddings"
            : $"{normalized}/v1/embeddings");
    }
}

internal interface ISemanticMemoryEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        SemanticMemorySettings settings,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken);
}

internal sealed class OpenAiSemanticMemoryEmbeddingClient(HttpClient httpClient) : ISemanticMemoryEmbeddingClient
{
    private const int MaximumBatchSize = 32;
    private const int MaximumDimensions = 8_192;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        SemanticMemorySettings settings,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        if (!SemanticMemorySettingsPolicy.TryValidate(settings, out var normalized, out var error) || !normalized.Enabled)
            throw new InvalidOperationException(error.Length == 0 ? "의미 기억이 비활성화되어 있습니다." : error);
        if (inputs.Count is < 1 or > MaximumBatchSize ||
            inputs.Any(input => string.IsNullOrWhiteSpace(input) || input.Length > SemanticMemorySettingsPolicy.MaximumInputCharacters))
            throw new ArgumentOutOfRangeException(nameof(inputs));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            SemanticMemorySettingsPolicy.ResolveEndpoint(normalized.BaseUrl));
        request.Content = JsonContent.Create(new { model = normalized.Model, input = inputs });
        if (!string.IsNullOrWhiteSpace(normalized.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalized.ApiKey);
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Embedding 응답이 허용 크기를 초과했습니다.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        await CopyBoundedAsync(stream, bounded, cancellationToken).ConfigureAwait(false);
        bounded.Position = 0;
        using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() != inputs.Count)
            throw new InvalidDataException("Embedding 응답 항목 수가 요청과 일치하지 않습니다.");

        var ordered = data.EnumerateArray()
            .Select(item => (Index: ReadIndex(item), Item: item))
            .OrderBy(item => item.Index)
            .ToArray();
        if (!ordered.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, inputs.Count)))
            throw new InvalidDataException("Embedding 응답 index가 요청 순서와 일치하지 않습니다.");
        var vectors = ordered.Select(item => ReadVector(item.Item)).ToArray();
        if (vectors.Select(vector => vector.Length).Distinct().Count() != 1)
            throw new InvalidDataException("Embedding 벡터 차원이 일치하지 않습니다.");
        return vectors;
    }

    private static float[] ReadVector(JsonElement item)
    {
        if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array ||
            embedding.GetArrayLength() is < 1 or > MaximumDimensions)
            throw new InvalidDataException("Embedding 벡터가 올바르지 않습니다.");
        var vector = embedding.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        if (vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Embedding 벡터에 유효하지 않은 값이 있습니다.");
        return vector;
    }

    private static int ReadIndex(JsonElement item)
    {
        if (!item.TryGetProperty("index", out var index) || !index.TryGetInt32(out var value))
            throw new InvalidDataException("Embedding 응답 index가 올바르지 않습니다.");
        return value;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > MaximumResponseBytes) throw new InvalidDataException("Embedding 응답이 허용 크기를 초과했습니다.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
