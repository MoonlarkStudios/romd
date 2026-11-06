using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>Original descriptor and decisions survive catalog and upload-history deletion.</summary>
public sealed class OriginalDiscLayoutEntity : EntityBase
{
    public Guid SourceJobId { get; set; }
    public string SourceRelativePath { get; set; } = null!;
    public string EntryPoint { get; set; } = null!;
    public int DescriptorFileId { get; set; }
    public string DependencyPathsJson { get; set; } = "[]";
    public string State { get; set; } = "pending";
    public string? Issue { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public int? PlatformId { get; set; }
    public int? ReleaseId { get; set; }
    public string? Fingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<OriginalDiscLayoutFileEntity> Files { get; set; } = [];
    public List<OriginalDiscLayoutAuditEntity> Audit { get; set; } = [];

    public static void Configure(EntityTypeBuilder<OriginalDiscLayoutEntity> builder)
    {
        builder.ConfigureEntity("OriginalDiscLayouts");
        builder.Property(x => x.SourceRelativePath).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.EntryPoint).HasMaxLength(4096).IsRequired();
        builder.Property(x => x.Fingerprint).HasMaxLength(120);
        builder.Property(x => x.State).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Issue).HasMaxLength(500);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => new { x.SourceJobId, x.SourceRelativePath }).IsUnique();
        builder.HasIndex(x => new { x.PlatformId, x.Fingerprint });
        builder.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(x => x.DescriptorFileId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("CK_OriginalDiscLayouts_State", "\"State\" IN ('pending','accepted','rejected','detached')"));
    }
}

/// <summary>Every retained candidate and historical confirmed dependency is an explicit CAS root.</summary>
public sealed class OriginalDiscLayoutFileEntity : EntityBase
{
    public int OriginalDiscLayoutId { get; set; }
    public string Path { get; set; } = null!;
    public int RomId { get; set; }
    public int FileId { get; set; }
    public Guid BindingRevision { get; set; }
    public static void Configure(EntityTypeBuilder<OriginalDiscLayoutFileEntity> builder)
    {
        builder.ConfigureEntity("OriginalDiscLayoutFiles");
        builder.Property(x => x.Path).HasMaxLength(4096).IsRequired();
        builder.HasOne<OriginalDiscLayoutEntity>().WithMany(x => x.Files).HasForeignKey(x => x.OriginalDiscLayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OriginalDiscLayoutId, x.BindingRevision, x.Path }).IsUnique();
    }
}

public sealed class OriginalDiscLayoutAuditEntity : EntityBase
{
    public int OriginalDiscLayoutId { get; set; }
    public Guid Revision { get; set; }
    public string Action { get; set; } = null!;
    public Guid? Actor { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Reason { get; set; }
    public static void Configure(EntityTypeBuilder<OriginalDiscLayoutAuditEntity> builder)
    {
        builder.ConfigureEntity("OriginalDiscLayoutAudit");
        builder.Property(x => x.Action).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1000);
        builder.HasOne<OriginalDiscLayoutEntity>().WithMany(x => x.Audit).HasForeignKey(x => x.OriginalDiscLayoutId).OnDelete(DeleteBehavior.Cascade);
    }
}
