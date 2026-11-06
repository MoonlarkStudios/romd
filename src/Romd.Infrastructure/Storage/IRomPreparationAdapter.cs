using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

internal interface IRomPreparationAdapter
{
    RomRepresentationEncoding Encoding { get; }
    bool RequiresDatHashes(ReadOnlySpan<byte> prefix);
    /// <summary>Container magic that claims an upload before any adapter claims by name; empty when there is none.</summary>
    ReadOnlySpan<byte> Signature { get; }
    bool Claims(ReadOnlySpan<byte> magic, string filename);
    Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, CancellationToken ct);
    Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename, CancellationToken ct);
}
