using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class ReferenceCatalogStateEntity
{
    public int Id { get; set; } = 1;
    public string Revision { get; set; } = null!;
    public int BuiltInVersion { get; set; }
    public string Json { get; set; } = null!;
    public static void Configure(EntityTypeBuilder<ReferenceCatalogStateEntity> b)
    {
        b.ToTable("ReferenceCatalogState", t => t.HasCheckConstraint("CK_ReferenceCatalogState_Singleton", "\"Id\" = 1"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Revision).HasMaxLength(64);
        b.Property(x => x.Json).IsRequired();
    }
}
