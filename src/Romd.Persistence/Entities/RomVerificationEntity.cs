using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;

namespace Romd.Persistence.Entities;

/// <summary>One immutable independent observation by an exact decoder build; outputs live in the measured child rows.</summary>
public sealed class RomVerificationEntity
{
    public Guid Id { get; set; }
    public int RepresentationId { get; set; }
    public Sha256 ArtifactSha256 { get; set; }
    public long ArtifactSize { get; set; }
    public string DecoderBuild { get; set; } = null!;
    public string? DecoderRid { get; set; }
    public Sha1? DecoderUpstreamCommit { get; set; }
    public int? DecoderRecipeVersion { get; set; }
    public string CheckedProfile { get; set; } = null!;
    public string VerifierBuild { get; set; } = null!;
    public long ObservedAtUtcTicks { get; set; }
    public Sha256? ObservedParametersSha256 { get; set; }
    public Sha1? ComputedRawSha1 { get; set; }
    public Sha1? ComputedOverallSha1 { get; set; }

    public static void Configure(EntityTypeBuilder<RomVerificationEntity> builder)
    {
        builder.ToTable("Verifications", table => table.ExcludeFromMigrations());
        builder.HasKey(row => row.Id);
        builder.Property(row => row.DecoderBuild).HasMaxLength(256).IsRequired();
        builder.Property(row => row.DecoderRid).HasMaxLength(32);
        builder.Property(row => row.CheckedProfile).HasMaxLength(128).IsRequired();
        builder.Property(row => row.VerifierBuild).HasMaxLength(256).IsRequired();
        builder.HasOne<RomRepresentationEntity>().WithMany().HasForeignKey(row => row.RepresentationId).OnDelete(DeleteBehavior.Cascade);
    }
}
