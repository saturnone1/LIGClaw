namespace LIGClaw.Application.Capabilities;

public interface ILigcapPackageCodec
{
    Task<CapabilityPackageReadResult> ReadAsync(Stream source, CancellationToken cancellationToken);

    Task WriteAsync(
        Stream destination,
        CapabilityPackageDocument document,
        CancellationToken cancellationToken);
}
