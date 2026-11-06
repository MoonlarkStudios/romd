using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.Entities;

public sealed class RatingBoardEntity
{
    public string Key { get; set; } = null!;
    public ReferenceOwnership Ownership { get; set; }
    public int? BuiltInVersion { get; set; }
    public string BaseName { get; set; } = null!;
    public string? BaseDescription { get; set; }
    public bool Retired { get; set; }
    public static void Configure(EntityTypeBuilder<RatingBoardEntity> b)
    {
        b.ToTable("RatingBoards", t => t.HasCheckConstraint("CK_RatingBoards_ReferenceOwnership", "(\"Ownership\" = 'Romd' AND \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0)"));
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.BaseName).HasMaxLength(200).IsRequired();
        b.Property(x => x.BaseDescription).HasMaxLength(2000);
    }
}
