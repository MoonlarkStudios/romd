using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     The single declaration of one stored representation encoding: the descriptor Application reads, how a host reads
///     it and how the worker prepares uploads of it. Facets are factories over
///     host dependencies, so a host that never builds a facet never constructs that facet's services.
/// </summary>
/// <param name="Reader">The reader adapter, or null when this host cannot read the encoding.</param>
/// <param name="ReadAdmission">What the content reader admits before it opens storage for this encoding.</param>
/// <param name="Preparation">Preparation adapters in claim order.</param>
internal sealed record RomEncodingEntry(
    RomEncodingDescriptor Descriptor,
    Func<RomReadDependencies, IRomMediaAdapter?> Reader,
    Func<RomReadAdmissionDependencies, RomReadAdmission> ReadAdmission,
    Func<RomPreparationDependencies, IReadOnlyList<IRomPreparationAdapter>> Preparation,
    RomPreparationSignature? PreparationSignature);

/// <param name="Chd">The host's CHD reader; null without configured native assets.</param>
internal sealed record RomReadDependencies(ChdMediaAdapter? Chd);

/// <summary>Worker-only preparation services, resolved lazily so building the registry starts no verifier.</summary>
/// <param name="Chd">The CHD DVD verifier; resolution refuses unavailable native assets.</param>
internal sealed record RomPreparationDependencies(
    IHashingService Hashing,
    Func<RvzVerifier> Rvz,
    Func<IRomOriginalMediaIdentifier> OriginalMedia,
    Func<ChdVerifier> Chd,
    Func<ChdCdImportInspector> Cd);
