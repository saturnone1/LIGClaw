using LIGClaw.Contracts.Generated;

namespace LIGClaw.Contracts.Protocol;

public static class ProtocolConstants
{
    public const string JsonRpcVersion = "2.0";
    public const string ProtocolVersion = ContractMetadata.ProtocolVersion;
    public const string ContractHash = ContractMetadata.Hash;
    public const int MaximumHeaderBytes = 8 * 1024;
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
}
