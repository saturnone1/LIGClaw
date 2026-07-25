using System.Text.Json;
using System.Text.Json.Serialization;

namespace LIGClaw.Contracts.Protocol;

public sealed record RpcRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] object? Params,
    [property: JsonPropertyName("jsonrpc")] string JsonRpc = ProtocolConstants.JsonRpcVersion);

public sealed record RpcResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] RpcError? Error,
    [property: JsonPropertyName("jsonrpc")] string JsonRpc = ProtocolConstants.JsonRpcVersion);

public sealed record RpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("hostVersion")] string HostVersion,
    [property: JsonPropertyName("contractHash")] string ContractHash,
    [property: JsonPropertyName("sessionToken")] string SessionToken);

public sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("sidecarVersion")] string SidecarVersion,
    [property: JsonPropertyName("contractHash")] string ContractHash,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record PingResult(
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);
