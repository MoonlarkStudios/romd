using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>CAS dependency projection of an immutable reconstruction member. Fact SQL owns the complete relational layout.</summary>
public sealed class RomReconstructionMemberEntity
{
    public int RepresentationId { get; set; }
    public int Ordinal { get; set; }
    public int? StoredFileId { get; set; }
    public static void Configure(EntityTypeBuilder<RomReconstructionMemberEntity> builder)
    {
        builder.ToTable("ReconstructionMembers", table => table.ExcludeFromMigrations());
        builder.HasKey(row => new { row.RepresentationId, row.Ordinal });
    }
}
