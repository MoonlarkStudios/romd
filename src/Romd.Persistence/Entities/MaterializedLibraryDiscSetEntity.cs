using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>Published ordered membership. Live admin corrections never mutate this snapshot.</summary>
public sealed class MaterializedLibraryDiscSetEntity : EntityBase
{
    public int LibraryId { get; set; }
    public int DiscSetId { get; set; }
    public int PlatformId { get; set; }
    public int TitleId { get; set; }
    public Guid Revision { get; set; }
    public string DefinitionJson { get; set; } = null!;

    public static void Configure(EntityTypeBuilder<MaterializedLibraryDiscSetEntity> builder)
    {
        builder.ConfigureEntity("MaterializedLibraryDiscSets");
        builder.Property(x => x.DefinitionJson).IsRequired().HasColumnType("TEXT");
        builder.HasIndex(x => new { x.LibraryId, x.DiscSetId }).IsUnique();
        builder.HasIndex(x => new { x.LibraryId, x.TitleId, x.DiscSetId });
        builder.HasOne<LibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed record PublishedDiscSetDefinition(string Name, int? ExpectedDiscCount,
    IReadOnlyList<PublishedDiscSetMember> Members);
public sealed record PublishedDiscSetMember(int ReleaseId, string Fingerprint, int Ordinal, int TitleId);
