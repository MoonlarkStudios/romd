using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Libraries;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class LibraryEntity : EntityBase, IEditableEntity
{
    public string Name { get; set; } = null!;
    public string ConfigurationJson { get; set; } = "{}";
    public string ConfigurationState { get; set; } = LibraryConfigurationState.Valid.ToString();
    public string? ConfigurationError { get; set; }
    public bool IsDefault { get; set; }
    public bool NeedsMaterialization { get; set; }
    public long MaterializationGeneration { get; set; }
    public Guid MaterializationRevision { get; set; } = Guid.NewGuid();
    public DateTimeOffset? LastMaterializedAt { get; set; }
    public int ItemCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<LibraryEntity> builder)
    {
        builder.ConfigureEntity("Libraries");

        builder.Property(l => l.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.HasIndex(l => l.Name)
            .IsUnique();

        builder.Property(l => l.IsDefault)
            .HasDefaultValue(false);

        builder.HasIndex(l => l.IsDefault)
            .IsUnique()
            .HasFilter("\"IsDefault\"")
            .HasDatabaseName("IX_Libraries_IsDefault_Unique");

        builder.Property(l => l.ConfigurationJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(l => l.ConfigurationState)
            .IsRequired()
            .HasMaxLength(50)
            .HasDefaultValue(LibraryConfigurationState.Valid.ToString());

        builder.Property(l => l.ConfigurationError)
            .HasMaxLength(2000);

        builder.HasIndex(l => l.ConfigurationState);
        builder.HasIndex(l => l.NeedsMaterialization);

        builder.Property(l => l.MaterializationRevision)
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(l => l.MaterializationGeneration)
            .HasDefaultValue(0L);

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_Libraries_MaterializationGeneration",
            "\"MaterializationGeneration\" >= 0"));
    }

    public Library ToDomain()
    {
        var parsed = DeserializeConfig(ConfigurationJson, ConfigurationState, ConfigurationError);
        return Library.RehydrateWithMaterializationGeneration(
            Id,
            Name,
            parsed.Configuration,
            parsed.State,
            parsed.Error,
            IsDefault,
            NeedsMaterialization,
            LastMaterializedAt,
            ItemCount,
            CreatedAt,
            UpdatedAt,
            MaterializationGeneration,
            MaterializationRevision);
    }

    public static LibraryEntity FromDomain(Library domain)
    {
        return new LibraryEntity
        {
            Id = domain.Id,
            Name = domain.Name,
            ConfigurationJson = JsonSerializer.Serialize(domain.Configuration),
            ConfigurationState = domain.ConfigurationState.ToString(),
            ConfigurationError = domain.ConfigurationError,
            IsDefault = domain.IsDefault,
            NeedsMaterialization = domain.NeedsMaterialization,
            MaterializationGeneration = domain.MaterializationGeneration,
            MaterializationRevision = domain.MaterializationRevision,
            LastMaterializedAt = domain.LastMaterializedAt,
            ItemCount = domain.ItemCount,
            CreatedAt = domain.CreatedAt,
            UpdatedAt = domain.UpdatedAt
        };
    }

    internal static LibraryConfiguration DeserializeConfig(string json)
    {
        return DeserializeConfig(json, LibraryConfigurationState.Valid.ToString(), null).Configuration;
    }

    internal static LibraryConfigurationReadResult DeserializeConfig(
        string json,
        string configurationState,
        string? configurationError)
    {
        try
        {
            var persistedState = Enum.TryParse<LibraryConfigurationState>(configurationState, out var state)
                ? state
                : LibraryConfigurationState.Invalid;

            if (persistedState != LibraryConfigurationState.Valid)
            {
                return new LibraryConfigurationReadResult(
                    LibraryConfiguration.InvalidFailClosedSentinel,
                    persistedState,
                    configurationError ?? "Library configuration is not valid.");
            }

            var configuration = JsonSerializer.Deserialize<LibraryConfiguration>(json);
            if (configuration is null)
                return Invalid("Library configuration JSON did not contain a configuration.");

            var validationError = LibraryConfigurationValidator.Validate(configuration);
            return validationError is not null
                ? Invalid(validationError)
                : new LibraryConfigurationReadResult(configuration, LibraryConfigurationState.Valid, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Invalid(ex.Message);
        }

        static LibraryConfigurationReadResult Invalid(string error) =>
            new(LibraryConfiguration.InvalidFailClosedSentinel, LibraryConfigurationState.Invalid, error);
    }

    internal sealed record LibraryConfigurationReadResult(
        LibraryConfiguration Configuration,
        LibraryConfigurationState State,
        string? Error);
}
