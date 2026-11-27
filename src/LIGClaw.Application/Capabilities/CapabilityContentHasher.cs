using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public static class CapabilityContentHasher
{
    public static CapabilityContentHash Compute(CapabilityVersionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "manifest.json", payload.CanonicalManifestJson);
        Append(hash, "instructions.md", NormalizeNewlines(payload.Instructions));
        Append(hash, "input.schema.json", payload.CanonicalInputSchemaJson);
        if (payload.CanonicalSettingsSchemaJson is not null)
            Append(hash, "settings.schema.json", payload.CanonicalSettingsSchemaJson);
        if (payload.CanonicalWorkflowJson is not null)
            Append(hash, "workflow.json", payload.CanonicalWorkflowJson);
        Append(hash, "tests.json", payload.CanonicalTestsJson);
        return CapabilityContentHash.Parse($"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
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

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

public enum CapabilityIngressViolation
{
    None,
    SecretLiteral,
    ConnectionLiteral,
}

public static partial class CapabilityIngressPolicy
{
    private const string JsonSchemaDeclaration = "https://json-schema.org/draft/2020-12/schema";

    public static CapabilityIngressViolation GetViolation(CapabilityVersionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (ContainsSecretLiteral(payload)) return CapabilityIngressViolation.SecretLiteral;
        return ContainsConnectionLiteral(payload)
            ? CapabilityIngressViolation.ConnectionLiteral
            : CapabilityIngressViolation.None;
    }

    public static bool ContainsSecretLiteral(CapabilityVersionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ContainsSecret(payload.CanonicalManifestJson) ||
               ContainsSecret(payload.Instructions) ||
               ContainsSecret(payload.CanonicalInputSchemaJson) ||
               ContainsSecret(payload.CanonicalSettingsSchemaJson) ||
               ContainsSecret(payload.CanonicalWorkflowJson) ||
               ContainsSecret(payload.CanonicalTestsJson) ||
               ContainsSensitiveJsonLiteral(payload.CanonicalManifestJson) ||
               ContainsSensitiveJsonLiteral(payload.CanonicalInputSchemaJson) ||
               ContainsSensitiveJsonLiteral(payload.CanonicalSettingsSchemaJson) ||
               ContainsSensitiveJsonLiteral(payload.CanonicalWorkflowJson) ||
               ContainsSensitiveJsonLiteral(payload.CanonicalTestsJson);
    }

    public static bool ContainsConnectionLiteral(CapabilityVersionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var manifest = payload.Manifest;
        return ContainsConnectionText(manifest.DisplayName) ||
               ContainsConnectionText(manifest.Description) ||
               manifest.IntentExamples.Any(ContainsConnectionText) ||
               manifest.RequiredTools.Any(ContainsConnectionText) ||
               ContainsConnectionText(payload.Instructions) ||
               ContainsConnectionJsonLiteral(payload.CanonicalManifestJson) ||
               ContainsConnectionJsonLiteral(payload.CanonicalInputSchemaJson) ||
               ContainsConnectionJsonLiteral(payload.CanonicalSettingsSchemaJson) ||
               ContainsConnectionJsonLiteral(payload.CanonicalWorkflowJson) ||
               ContainsConnectionJsonLiteral(payload.CanonicalTestsJson);
    }

    public static bool ContainsSensitiveJsonLiteral(JsonElement root) =>
        ContainsSensitiveJsonLiteralCore(root, sensitiveContext: false);

    private static bool ContainsSecret(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var scrubbed = AllowedPlaceholderFragment().Replace(value, string.Empty);
        return scrubbed.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase) ||
               TokenPrefix().IsMatch(scrubbed) ||
               AuthorizationHeader().IsMatch(scrubbed) ||
               SecretAssignment().IsMatch(scrubbed) ||
               Jwt().IsMatch(scrubbed) ||
               UriUserInfo().IsMatch(scrubbed);
    }

    private static bool ContainsSensitiveJsonLiteral(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return ContainsSensitiveJsonLiteralCore(document.RootElement, sensitiveContext: false);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsConnectionText(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (AbsoluteHttpUri().IsMatch(value) ||
         CredentialManagerTarget().IsMatch(value) ||
         EndpointComponentStatement().IsMatch(value) ||
         ContainsAssignedEndpointComponentSet(value) ||
         ConnectionAssignment().IsMatch(value) ||
         AuthenticationModeStatement().IsMatch(value));

    private static bool ContainsAssignedEndpointComponentSet(string value) =>
        HostAssignment().IsMatch(value) &&
        (PortAssignment().IsMatch(value) || SchemeAssignment().IsMatch(value));

    private static bool ContainsConnectionJsonLiteral(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return ContainsConnectionJsonLiteralCore(
                document.RootElement,
                connectionContext: false,
                propertyName: null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsConnectionJsonLiteralCore(
        JsonElement element,
        bool connectionContext,
        string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (ContainsEndpointComponentSet(element)) return true;
                foreach (var property in element.EnumerateObject())
                {
                    var connectionField = IsConnectionFieldName(property.Name);
                    if ((connectionField ||
                         connectionContext && IsConnectionContextFieldName(property.Name)) &&
                        ContainsDirectConnectionValue(property.Value))
                        return true;
                    if (ContainsConnectionJsonLiteralCore(
                            property.Value,
                            connectionContext || connectionField,
                            property.Name))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsConnectionJsonLiteralCore(item, connectionContext, propertyName)) return true;
                }
                return false;
            case JsonValueKind.String:
                var text = element.GetString();
                if (StringComparer.Ordinal.Equals(propertyName, "$schema") &&
                    StringComparer.Ordinal.Equals(text, JsonSchemaDeclaration))
                    return false;
                return ContainsConnectionText(text) ||
                       connectionContext && IsAuthenticationModeLiteral(text);
            default:
                return false;
        }
    }

    private static bool ContainsDirectConnectionValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return !string.IsNullOrWhiteSpace(value.GetString());
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ContainsDirectConnectionValue(item)) return true;
                }
                return false;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if ((IsLiteralBearingKeyword(property.Name) ||
                         IsConnectionContextFieldName(property.Name)) &&
                        ContainsDirectConnectionValue(property.Value))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool ContainsEndpointComponentSet(JsonElement value)
    {
        var hasHost = false;
        var hasPort = false;
        var hasScheme = false;
        foreach (var property in value.EnumerateObject())
        {
            var component = GetEndpointComponent(property.Name);
            if (component == 0 || !ContainsEndpointComponentLiteral(property.Value, component)) continue;
            switch (component)
            {
                case 1:
                    hasHost = true;
                    break;
                case 2:
                    hasPort = true;
                    break;
                case 3:
                    hasScheme = true;
                    break;
            }
        }
        return hasHost && (hasPort || hasScheme);
    }

    private static bool ContainsEndpointComponentLiteral(JsonElement value, int component)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) return false;
                return component switch
                {
                    1 => true,
                    2 => IsPortLiteral(text),
                    3 => text is "http" or "https",
                    _ => false,
                };
            case JsonValueKind.Number:
                return component == 2 && value.TryGetInt32(out var numericPort) &&
                       numericPort is >= 1 and <= 65_535;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ContainsEndpointComponentLiteral(item, component)) return true;
                }
                return false;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (IsLiteralBearingKeyword(property.Name) &&
                        ContainsEndpointComponentLiteral(property.Value, component))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool IsPortLiteral(string value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port) &&
        port is >= 1 and <= 65_535;

    private static int GetEndpointComponent(string name)
    {
        Span<char> normalized = stackalloc char[name.Length];
        var length = NormalizeFieldName(name, normalized);
        return normalized[..length] switch
        {
            "host" or "hostname" => 1,
            "port" => 2,
            "scheme" => 3,
            _ => 0,
        };
    }

    private static bool ContainsSensitiveJsonLiteralCore(JsonElement element, bool sensitiveContext)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var sensitiveField = IsSensitiveFieldName(property.Name);
                    if (sensitiveField &&
                        ContainsDirectSensitiveValue(property.Value))
                        return true;
                    if (sensitiveContext && IsLiteralBearingKeyword(property.Name) &&
                        ContainsNonPlaceholderLiteral(property.Value))
                        return true;
                    if (ContainsSensitiveJsonLiteralCore(
                            property.Value,
                            sensitiveContext || sensitiveField))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsSensitiveJsonLiteralCore(item, sensitiveContext)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool ContainsDirectSensitiveValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (IsLiteralBearingKeyword(property.Name) &&
                    ContainsNonPlaceholderLiteral(property.Value))
                    return true;
            }
            return false;
        }
        return ContainsNonPlaceholderLiteral(value);
    }

    private static bool ContainsNonPlaceholderLiteral(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                return !string.IsNullOrWhiteSpace(text) && !AllowedSecretPlaceholder().IsMatch(text.Trim());
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ContainsNonPlaceholderLiteral(item)) return true;
                }
                return false;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (ContainsNonPlaceholderLiteral(property.Value)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool IsSensitiveFieldName(string name)
    {
        Span<char> normalized = stackalloc char[name.Length];
        var length = 0;
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character)) normalized[length++] = char.ToLowerInvariant(character);
        }
        return normalized[..length] switch
        {
            "password" or "passwd" or "pwd" or "passphrase" or "secret" or "apikey" or
            "apisecret" or "clientsecret" or "token" or "accesstoken" or "refreshtoken" or
            "idtoken" or "bearertoken" or "authorization" or "credential" or "credentials" or
            "privatekey" or "signingkey" => true,
            _ => false,
        };
    }

    private static bool IsConnectionFieldName(string name)
    {
        Span<char> normalized = stackalloc char[name.Length];
        var length = NormalizeFieldName(name, normalized);
        return normalized[..length] switch
        {
            "endpoint" or "endpointurl" or "baseendpoint" or "baseurl" or
            "profileid" or "connectionprofileid" or "httpprofileid" or
            "credentialtarget" or "windowscredentialtarget" or
            "auth" or "authkind" or "authmode" or "authtype" or
            "authentication" or "authenticationkind" or "authenticationmode" or
            "authenticationtype" => true,
            _ => false,
        };
    }

    private static bool IsConnectionContextFieldName(string name)
    {
        Span<char> normalized = stackalloc char[name.Length];
        var length = NormalizeFieldName(name, normalized);
        return normalized[..length] switch
        {
            "host" or "hostname" or "port" or "scheme" or "url" or "uri" or
            "id" or "kind" or "mode" or "value" => true,
            _ => false,
        };
    }

    private static int NormalizeFieldName(string name, Span<char> normalized)
    {
        var length = 0;
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character)) normalized[length++] = char.ToLowerInvariant(character);
        }
        return length;
    }

    private static bool IsAuthenticationModeLiteral(string? value) =>
        value?.Trim().ToLowerInvariant() is "none" or "bearer" or "basic" or "oauth" or "oauth2";

    private static bool IsLiteralBearingKeyword(string name) =>
        name is "const" or "enum" or "default" or "examples";

    [GeneratedRegex(@"(?:sk-|nvapi-|ghp_|github_pat_|xox[a-z]-|AKIA)[A-Za-z0-9_\-]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefix();

    [GeneratedRegex(@"Authorization\s*:\s*(?:Bearer|Basic)\s+[A-Za-z0-9+/_.=\-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?:password|pwd|api[_-]?key|client[_-]?secret|token)\s*[:=]\s*[^\s\""']{6,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"https?://[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfo();

    [GeneratedRegex(@"^(?:\$\{fixture:[a-z0-9._-]{1,64}\}|<redacted>|\[REDACTED\])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedSecretPlaceholder();

    [GeneratedRegex(@"\$\{fixture:[a-z0-9._-]{1,64}\}|<redacted>|\[REDACTED\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPlaceholderFragment();

    [GeneratedRegex(@"(?<![a-z0-9+.-])https?://[^\s<>\""']{1,2048}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteHttpUri();

    [GeneratedRegex(@"\bLIGClaw[\\/]Connections[\\/][a-z0-9._-]{1,64}[\\/](?:BearerToken|Password|ApiKey)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialManagerTarget();

    [GeneratedRegex(@"\bhost\s+(?:localhost|[a-z0-9-]+(?:\.[a-z0-9-]+)+|(?:[0-9]{1,3}\.){3}[0-9]{1,3}|\[[0-9a-f:]+\])\s+(?:on\s+)?port\s+[0-9]{1,5}\s+(?:over|using)\s+https?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndpointComponentStatement();

    [GeneratedRegex(@"\bhost(?:name)?\s*[:=]\s*(?:localhost|[a-z0-9-]+(?:\.[a-z0-9-]+)+|(?:[0-9]{1,3}\.){3}[0-9]{1,3}|\[[0-9a-f:]+\])(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostAssignment();

    [GeneratedRegex(@"\bport\s*[:=]\s*[0-9]{1,5}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortAssignment();

    [GeneratedRegex(@"\bscheme\s*[:=]\s*https?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeAssignment();

    [GeneratedRegex(@"\b(?:endpoint(?:[\s_-]*url)?|base[\s_-]*(?:url|endpoint)|profile[\s_-]*id|connection[\s_-]*profile(?:[\s_-]*id)?|credential[\s_-]*target|auth(?:entication|orization)?(?:[\s_-]*(?:kind|mode|type))?)\s*[:=]\s*(?:\""[^\""\r\n]{1,512}\""|'[^'\r\n]{1,512}'|[^\s,;)}\]]{1,512})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionAssignment();

    [GeneratedRegex(@"\b(?:(?:auth(?:entication)?(?:[\s_-]+(?:kind|mode|type))?)\s+(?:is|uses?)\s+(?:none|bearer|basic|oauth2?)|(?:bearer|basic|oauth2?)\s+auth(?:entication)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticationModeStatement();
}
