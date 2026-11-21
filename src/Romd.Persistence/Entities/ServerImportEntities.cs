using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Admin.Application.Ingestion.Import;

namespace Romd.Persistence.Entities;

internal sealed class ServerImportActivityEntity
{
    public string DirectoryKey { get; set; } = null!;
    public DateTimeOffset CheckedAt { get; set; }
    public ServerImportCheck Outcome { get; set; }
    public int WaitingFiles { get; set; }
    public string? Message { get; set; }
    public Guid? JobId { get; set; }
    public int LastQueuedFiles { get; set; }

    internal static void Configure(EntityTypeBuilder<ServerImportActivityEntity> builder)
    {
        builder.ToTable("ServerImportActivity");
        builder.HasKey(row => row.DirectoryKey);
        builder.Property(row => row.DirectoryKey).HasMaxLength(64);
    }
}

internal sealed class ServerImportSelectionEntity
{
    public Guid JobId { get; set; }
    public string FilesJson { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<ServerImportSelectionEntity> builder)
    {
        builder.ToTable("ServerImportSelections");
        builder.HasKey(row => row.JobId);
        builder.Property(row => row.JobId).ValueGeneratedNever();
        builder.Property(row => row.FilesJson).HasColumnType("jsonb");
        builder.HasOne<JobEntity>().WithOne().HasForeignKey<ServerImportSelectionEntity>(row => row.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
