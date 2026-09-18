using Romd.Application.Common.Systems;
using Romd.Application.Common.Ids;
using Romd.Domain.Jobs;
using Models = Romd.Contracts.Management.Models;
using Romd.Contracts.Common.Models;

namespace Romd.Admin.Application.Ingestion.Jobs;

public static class JobItemMapping
{
    public static Models.JobItemDto ToContract(this JobItemView view, SystemKeys systemKeys) => new()
    {
        Id = view.Id.ToString(),
        Kind = view.Kind == JobItemKind.Dat ? "dat" : "rom",
        FileName = view.FileName,
        SizeBytes = (ByteCount)view.SizeBytes,
        Outcome = OutcomeString(view.Outcome),
        RomFileId = view.RomFileId.HasValue ? IdCoder.Encode(view.RomFileId.Value) : null,
        DatFileId = view.DatFileId.HasValue ? IdCoder.Encode(view.DatFileId.Value) : null,
        SystemKey = systemKeys.Optional(view.PlatformId),
        PlatformName = view.PlatformName,
        MatchedTitles = view.MatchedTitles
            .Select(t => new Models.JobItemTitleDto(IdCoder.Encode(t.TitleId), t.Name))
            .ToList(),
        GameCount = view.GameCount,
        ArchiveOnly = view.ArchiveOnly,
        Error = view.Error,
        CreatedAt = view.CreatedAt
    };

    private static string OutcomeString(JobItemOutcome outcome) => outcome switch
    {
        JobItemOutcome.Ingested => "ingested",
        JobItemOutcome.Deduplicated => "deduplicated",
        JobItemOutcome.Rejected => "rejected",
        JobItemOutcome.Failed => "failed",
        JobItemOutcome.DatRouted => "dat_routed",
        JobItemOutcome.DatUnrouted => "dat_unrouted",
        _ => "failed"
    };

    /// <summary>Parses the outcome filter query value back to the domain enum (null if unrecognized).</summary>
    public static JobItemOutcome? ParseOutcome(string? value) => value switch
    {
        "ingested" => JobItemOutcome.Ingested,
        "deduplicated" => JobItemOutcome.Deduplicated,
        "rejected" => JobItemOutcome.Rejected,
        "failed" => JobItemOutcome.Failed,
        "dat_routed" => JobItemOutcome.DatRouted,
        "dat_unrouted" => JobItemOutcome.DatUnrouted,
        _ => null
    };
}
