using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.Entities;

public sealed class CompanyEntity
{
    public string Key { get; set; } = null!;
    public ReferenceOwnership Ownership { get; set; }
    public int? BuiltInVersion { get; set; }
    public string BaseName { get; set; } = null!;
    public string? BaseDescription { get; set; }
    public bool Retired { get; set; }

    public string? NameOverride { get; set; }
    public bool HasDescriptionOverride { get; set; }
    public string? DescriptionOverride { get; set; }
    public string Name { get; set; } = null!;
    [NotMapped]
    public CompanyOverrides Overrides
    {
        get => new(NameOverride, DescriptionOverride, HasDescriptionOverride);
        set
        {
            NameOverride = value.Name;
            HasDescriptionOverride = value.HasDescription; DescriptionOverride = value.HasDescription ? value.Description : null;
        }
    }
    public static void Configure(EntityTypeBuilder<CompanyEntity> b)
    {
        b.ToTable("Companies");
        b.ToTable("Companies", t =>
        {
            t.HasCheckConstraint("CK_Companies_DescriptionOverride", "\"HasDescriptionOverride\" OR \"DescriptionOverride\" IS NULL");
        });
        b.Property(x => x.NameOverride).HasMaxLength(200);
        b.Property(x => x.DescriptionOverride).HasMaxLength(2000);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(64);
        b.ToTable("Companies", t => t.HasCheckConstraint("CK_Companies_ReferenceOwnership", "(\"Ownership\" = 'Romd' AND \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0) OR (\"Ownership\" = 'Installation' AND \"BuiltInVersion\" IS NULL)"));
        b.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.BaseName).HasMaxLength(200).IsRequired();
        b.Property(x => x.BaseDescription).HasMaxLength(2000);
    }
}
public sealed class SystemCompanyEntity
{
    public int PlatformId { get; set; }
    public string CompanyKey { get; set; } = null!;
    public PlatformEntity Platform { get; set; } = null!;
    public CompanyEntity Company { get; set; } = null!;
    public static void Configure(EntityTypeBuilder<SystemCompanyEntity> b)
    {
        b.ToTable("SystemCompanies");
        b.HasKey(x => new { x.PlatformId, x.CompanyKey });
        b.Property(x => x.CompanyKey).HasMaxLength(64);
        b.HasOne(x => x.Platform).WithMany().HasForeignKey(x => x.PlatformId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyKey).OnDelete(DeleteBehavior.Restrict);
    }
}
