using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Application.Common.Security;

namespace Romd.Persistence.Entities;

public sealed class ConsumerWebAppRegistrationEntity
{
    public required string ClientId { get; init; }
    public required string DisplayName { get; init; }
    public required string CallbackUri { get; init; }
    public required string BrowserOrigin { get; init; }
    public DateTimeOffset? ProvisionedAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }

    public ConsumerWebAppRegistration ToRegistration() =>
        new(ClientId, DisplayName, CallbackUri, BrowserOrigin, ProvisionedAt, DisabledAt);

    public static void Configure(EntityTypeBuilder<ConsumerWebAppRegistrationEntity> builder)
    {
        builder.ToTable("ConsumerWebAppRegistrations");
        builder.HasKey(item => item.ClientId);
        builder.Property(item => item.ClientId).HasMaxLength(100);
        builder.Property(item => item.DisplayName).HasMaxLength(120);
        builder.Property(item => item.CallbackUri).HasMaxLength(2048);
        builder.Property(item => item.BrowserOrigin).HasMaxLength(255);
        builder.HasIndex(item => item.BrowserOrigin)
            .HasFilter(""" "ProvisionedAt" IS NOT NULL AND "DisabledAt" IS NULL """);
    }
}
