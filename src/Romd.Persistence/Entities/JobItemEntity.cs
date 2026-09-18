using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

/// <summary>
///     Per-file provenance for an upload job. One row per ROM or DAT the job touched.
/// </summary>
public sealed class JobItemEntity : GuidEntityBase
{
    public Guid JobId { get; set; }
    public int Kind { get; set; }
    public string FileName { get; set; } = null!;
    public long SizeBytes { get; set; }
    public int Outcome { get; set; }
    public int? RomFileId { get; set; }
    public int? DatFileId { get; set; }
    public int? PlatformId { get; set; }
    public string MatchedTitleIdsJson { get; set; } = "[]";
    public int? GameCount { get; set; }
    public bool? ArchiveOnly { get; set; }
    public string? Error { get; set; }

    private IReadOnlyList<int> GetMatchedTitleIds()
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(MatchedTitleIdsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public JobItem ToDomain() => JobItem.Rehydrate(
        Id,
        JobId,
        (JobItemKind)Kind,
        FileName,
        SizeBytes,
        (JobItemOutcome)Outcome,
        RomFileId,
        DatFileId,
        PlatformId,
        GetMatchedTitleIds(),
        GameCount,
        Error,
        ArchiveOnly,
        CreatedAt);

    public static JobItemEntity FromDomain(JobItem item) => new()
    {
        Id = item.Id,
        JobId = item.JobId,
        Kind = (int)item.Kind,
        FileName = item.FileName,
        SizeBytes = item.SizeBytes,
        Outcome = (int)item.Outcome,
        RomFileId = item.RomFileId,
        DatFileId = item.DatFileId,
        PlatformId = item.PlatformId,
        MatchedTitleIdsJson = JsonSerializer.Serialize(item.MatchedTitleIds),
        GameCount = item.GameCount,
        ArchiveOnly = item.ArchiveOnly,
        Error = item.Error,
        CreatedAt = item.CreatedAt
    };

    public static void Configure(EntityTypeBuilder<JobItemEntity> builder)
    {
        builder.ToTable("JobItems");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.FileName)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(i => i.MatchedTitleIdsJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(i => i.Error)
            .HasMaxLength(2000);

        // Cascade with the parent job — provenance dies with the job it describes.
        builder.HasOne<JobEntity>()
            .WithMany()
            .HasForeignKey(i => i.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Keyset pagination order for the ledger.
        builder.HasIndex(i => new { i.JobId, i.CreatedAt, i.Id });
    }
}
