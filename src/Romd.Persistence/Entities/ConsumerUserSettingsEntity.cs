using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Identity;

namespace Romd.Persistence.Entities;

public sealed class ConsumerUserSettingsEntity
{
    public Guid UserId { get; init; }
    public string Theme { get; set; } = "system";
    public string PreferredRegionIdsJson { get; set; } = "[]";
    public string PreferredLanguageIdsJson { get; set; } = "[]";
    public string RevisionPreference { get; set; } = "None";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static void Configure(EntityTypeBuilder<ConsumerUserSettingsEntity> entity)
    {
        entity.ToTable("ConsumerUserSettings");
        entity.HasKey(settings => settings.UserId);

        entity.Property(settings => settings.Theme)
            .IsRequired()
            .HasMaxLength(20);

        entity.Property(settings => settings.PreferredRegionIdsJson)
            .IsRequired()
            .HasColumnType("TEXT");

        entity.Property(settings => settings.PreferredLanguageIdsJson)
            .IsRequired()
            .HasColumnType("TEXT");

        entity.Property(settings => settings.RevisionPreference)
            .IsRequired()
            .HasMaxLength(20);

        entity.HasOne<RomdUser>()
            .WithOne()
            .HasForeignKey<ConsumerUserSettingsEntity>(settings => settings.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
