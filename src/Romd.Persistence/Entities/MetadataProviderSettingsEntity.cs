using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class MetadataProviderSettingsEntity
{
    public string ProviderId { get; set; } = "igdb";
    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    public string? ProtectedClientSecret { get; set; }
    public Guid Revision { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastTestMessage { get; set; }
    public string? TestConfigurationFingerprint { get; set; }

    public static void Configure(EntityTypeBuilder<MetadataProviderSettingsEntity> builder)
    {
        builder.ToTable("MetadataProviderSettings");
        builder.HasKey(x => x.ProviderId);
        builder.Property(x => x.ProviderId).HasMaxLength(64);
        builder.Property(x => x.ClientId).HasMaxLength(256);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.Property(x => x.LastTestMessage).HasMaxLength(512);
        builder.Property(x => x.TestConfigurationFingerprint).HasMaxLength(64);
        builder.HasData(
            new MetadataProviderSettingsEntity { ProviderId = "igdb", Revision = Guid.Empty },
            new MetadataProviderSettingsEntity { ProviderId = "steamgriddb", Revision = Guid.Empty });
    }
}
