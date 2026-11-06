using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Libraries;

public class LibraryTests
{
    private static readonly LibraryConfiguration DefaultConfig = new();

    [Fact]
    public void DefaultConfiguration_ContentRatingPolicy_AllowsAdultsOnly()
    {
        DefaultConfig.ContentRatingPolicy.MaxMinimumAge.ShouldBe(18);
    }

    [Fact]
    public void DefaultConfiguration_ContentRatingPolicyUnknownRatingPolicy_NeedsReview()
    {
        DefaultConfig.ContentRatingPolicy.UnknownRatingPolicy.ShouldBe(UnknownMetadataPolicy.NeedsReview);
    }

    [Fact]
    public void DefaultConfiguration_TitleSelectionMode_Rules()
    {
        DefaultConfig.TitleSelectionMode.ShouldBe(LibraryTitleSelectionMode.Rules);
    }

    [Fact]
    public void LibraryConfigurationValidator_ValidConfiguration_ReturnsNull()
    {
        var config = new LibraryConfiguration
        {
            AllowedPlatformIds = [1],
            ExcludedDatIds = [2],
            IncludeTitleIds = [5],
            ExcludeTitleIds = [6]
        };

        LibraryConfigurationValidator.Validate(config).ShouldBeNull();
    }

    [Fact]
    public void LibraryConfigurationValidator_InvalidEnum_ReturnsError()
    {
        var config = DefaultConfig with
        {
            ContentRatingPolicy = DefaultConfig.ContentRatingPolicy with
            {
                UnknownRatingPolicy = (UnknownMetadataPolicy)999
            }
        };

        LibraryConfigurationValidator.Validate(config).ShouldBe("Unknown rating policy is invalid.");
    }

    [Fact]
    public void LibraryConfigurationValidator_InvalidTitleSelectionMode_ReturnsError()
    {
        var config = DefaultConfig with
        {
            TitleSelectionMode = (LibraryTitleSelectionMode)999
        };

        LibraryConfigurationValidator.Validate(config).ShouldBe("Title selection mode is invalid.");
    }

    [Fact]
    public void LibraryConfigurationValidator_InvalidIdentifier_ReturnsError()
    {
        var config = DefaultConfig with
        {
            AllowedPlatformIds = [0]
        };

        LibraryConfigurationValidator.Validate(config).ShouldBe(
            "Allowed platform IDs must be valid positive identifiers.");
    }

    [Fact]
    public void CreateNew_SetsInitialState()
    {
        var library = Library.CreateNew("My Library", DefaultConfig, now: DateTimeOffset.UnixEpoch);

        library.Id.ShouldBe(0);
        library.Name.ShouldBe("My Library");
        library.Configuration.ShouldBe(DefaultConfig);
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Valid);
        library.ConfigurationError.ShouldBeNull();
        library.IsDefault.ShouldBeFalse();
        library.NeedsMaterialization.ShouldBeTrue();
        library.LastMaterializedAt.ShouldBeNull();
        library.ItemCount.ShouldBe(0);
        library.CreatedAt.ShouldNotBe(default);
        library.UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public void CreateNew_TrimsName()
    {
        var library = Library.CreateNew("  My Library  ", DefaultConfig, now: DateTimeOffset.UnixEpoch);

        library.Name.ShouldBe("My Library");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNew_EmptyName_Throws(string? name)
    {
        Should.Throw<ArgumentException>(() => Library.CreateNew(name!, DefaultConfig, now: DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void CreateNew_NullConfiguration_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Library.CreateNew("Test", null!, now: DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Rehydrate_RestoresAllProperties()
    {
        var config = new LibraryConfiguration
        {
            AllowedPlatformIds = [1, 2],
            ContentRatingPolicy = new ContentRatingPolicy { MaxMinimumAge = 16 }
        };
        var createdAt = DateTimeOffset.UtcNow.AddDays(-7);
        var materializedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var updatedAt = DateTimeOffset.UtcNow.AddDays(-1);

        var library = Library.RehydrateWithMaterializationGeneration(
            id: 42,
            name: "Restored Library",
            configuration: config,
            configurationState: LibraryConfigurationState.Valid,
            configurationError: null,
            isDefault: true,
            needsMaterialization: false,
            lastMaterializedAt: materializedAt,
            itemCount: 150,
            createdAt: createdAt,
            updatedAt: updatedAt,
            materializationGeneration: 9);

        library.Id.ShouldBe(42);
        library.Name.ShouldBe("Restored Library");
        library.Configuration.ShouldBe(config);
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Valid);
        library.ConfigurationError.ShouldBeNull();
        library.IsDefault.ShouldBeTrue();
        library.NeedsMaterialization.ShouldBeFalse();
        library.LastMaterializedAt.ShouldBe(materializedAt);
        library.ItemCount.ShouldBe(150);
        library.CreatedAt.ShouldBe(createdAt);
        library.UpdatedAt.ShouldBe(updatedAt);
        library.MaterializationGeneration.ShouldBe(9);
    }

    [Fact]
    public void UpdateConfiguration_ChangesNameAndConfig_FlagsForRematerialization()
    {
        var library = Library.CreateNew("Original", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.MarkMaterialized(100, now: DateTimeOffset.UnixEpoch);
        library.NeedsMaterialization.ShouldBeFalse();

        var newConfig = new LibraryConfiguration
        {
            AllowedPlatformIds = [1, 2, 3],
            ShowMissingGames = true
        };

        library.UpdateConfiguration("Updated", newConfig, now: DateTimeOffset.UnixEpoch);

        library.Name.ShouldBe("Updated");
        library.Configuration.ShouldBe(newConfig);
        library.NeedsMaterialization.ShouldBeTrue();
        library.UpdatedAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateConfiguration_EmptyName_Throws(string? name)
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);

        Should.Throw<ArgumentException>(() => library.UpdateConfiguration(name!, DefaultConfig, now: DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void UpdateConfiguration_NullConfiguration_Throws()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);

        Should.Throw<ArgumentNullException>(() => library.UpdateConfiguration("Test", null!, now: DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void MarkMaterialized_ClearsFlag_SetsCountAndTimestamp()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.NeedsMaterialization.ShouldBeTrue();

        library.MarkMaterialized(250, now: DateTimeOffset.UnixEpoch);

        library.NeedsMaterialization.ShouldBeFalse();
        library.ItemCount.ShouldBe(250);
        library.LastMaterializedAt.ShouldNotBeNull();
    }

    [Fact]
    public void FlagForRematerialization_SetsFlag()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.MarkMaterialized(100, now: DateTimeOffset.UnixEpoch);
        library.NeedsMaterialization.ShouldBeFalse();

        library.FlagForRematerialization(now: DateTimeOffset.UnixEpoch);

        library.NeedsMaterialization.ShouldBeTrue();
        library.UpdatedAt.ShouldNotBeNull();
    }

    [Fact]
    public void AwaitsFirstPublication_OnlyUntilTheFirstSuccessfulRefresh()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.AwaitsFirstPublication.ShouldBeTrue();

        library.MarkMaterialized(100, now: DateTimeOffset.UnixEpoch);
        library.AwaitsFirstPublication.ShouldBeFalse();

        library.UpdateConfiguration("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.NeedsMaterialization.ShouldBeTrue();
        library.AwaitsFirstPublication.ShouldBeFalse();
    }

    [Fact]
    public void MarkConfigurationInvalid_SetsInvalidStateAndClearsMaterializationState()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.MarkMaterialized(100, now: DateTimeOffset.UnixEpoch);

        library.MarkConfigurationInvalid("Invalid config.", now: DateTimeOffset.UnixEpoch);

        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid);
        library.ConfigurationError.ShouldBe("Invalid config.");
        library.NeedsMaterialization.ShouldBeFalse();
        library.ItemCount.ShouldBe(0);
        library.UpdatedAt.ShouldNotBeNull();
    }

    [Fact]
    public void MarkAsDefault_SetsFlagAndUpdatedAt()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);

        library.MarkAsDefault(now: DateTimeOffset.UnixEpoch);

        library.IsDefault.ShouldBeTrue();
        library.UpdatedAt.ShouldNotBeNull();
    }

    [Fact]
    public void UnmarkAsDefault_ClearsFlagAndUpdatedAt()
    {
        var library = Library.CreateNew("Test", DefaultConfig, now: DateTimeOffset.UnixEpoch);
        library.MarkAsDefault(now: DateTimeOffset.UnixEpoch);

        library.UnmarkAsDefault(now: DateTimeOffset.UnixEpoch);

        library.IsDefault.ShouldBeFalse();
        library.UpdatedAt.ShouldNotBeNull();
    }
}
