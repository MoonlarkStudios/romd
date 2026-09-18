using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class GameLanguageEntity : EntityBase, ICreatableEntity
{
    public ReferenceOwnership? Ownership { get; set; }
    public int? BuiltInVersion { get; set; }
    public string? BaseName { get; set; }
    public string? BaseDescription { get; set; }
    public int BaseSortOrder { get; set; }
    public bool Retired { get; set; }
    // Effective compatibility facts are refreshed only by catalog publication for registered rows.
    public string? CanonicalKey { get; set; }
    public string Name { get; set; } = null!;
    public string Code { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsAutoCreated { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<GameLanguageEntity> builder)
    {
        builder.ConfigureEntity("GameLanguages");
        builder.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.BaseName).HasMaxLength(100);
        builder.Property(x => x.BaseDescription).HasMaxLength(2000);
        builder.ToTable("GameLanguages", t =>
        {
            t.HasCheckConstraint("CK_GameLanguages_ReferenceIdentity", "(\"CanonicalKey\" IS NULL AND \"Ownership\" IS NULL AND \"BaseName\" IS NULL AND \"BaseDescription\" IS NULL AND NOT \"Retired\" AND \"BaseSortOrder\" = 0) OR (\"CanonicalKey\" IS NOT NULL AND \"Ownership\" IS NOT NULL AND \"BaseName\" IS NOT NULL)");
            t.HasCheckConstraint("CK_GameLanguages_ReferenceOwnership", "CASE WHEN \"Ownership\" IS NULL THEN \"BuiltInVersion\" IS NULL WHEN \"Ownership\" = 'Romd' THEN \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0 ELSE false END");
            t.HasCheckConstraint("CK_GameLanguages_BaseSortOrder", "\"BaseSortOrder\" >= 0");
        });
        builder.Property(x => x.CanonicalKey).HasMaxLength(64);
        builder.HasIndex(x => x.CanonicalKey).IsUnique();

        builder.Property(l => l.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(l => l.Code)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(l => l.SortOrder)
            .IsRequired();

        builder.Property(l => l.IsAutoCreated)
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasIndex(l => l.Name)
            .HasDatabaseName("IX_GameLanguages_Name");

        builder.HasIndex(l => l.Code)
            .HasDatabaseName("IX_GameLanguages_Code");
    }

    public GameLanguage ToDomain() =>
        GameLanguage.Rehydrate(Id, Name, Code, SortOrder, IsAutoCreated);

    public static GameLanguageEntity FromDomain(GameLanguage domain) =>
        new()
        {
            Id = domain.Id,
            Name = domain.Name,
            Code = domain.Code,
            SortOrder = domain.SortOrder,
            IsAutoCreated = domain.IsAutoCreated
        };
}
