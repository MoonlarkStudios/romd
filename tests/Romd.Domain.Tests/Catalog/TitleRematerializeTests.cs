using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleRematerializeTests
{
    #region Helpers

    private static Title CreateTitle(
        IEnumerable<TitleMetadataLayer>? layers = null,
        Dictionary<string, string>? fieldSourceOverrides = null)
    {
        return Title.Rehydrate(
            id: 1,
            platformId: 10,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.None,
            lastEnrichedAt: null,
            createdAt: DateTimeOffset.UtcNow,
            metadataLayers: layers,
            fieldSourceOverrides: fieldSourceOverrides);
    }

    private static TitleMetadataLayer CreateLayer(
        string sourceId,
        MetadataSourceType sourceType,
        TitleMetadataPayload payload)
    {
        return TitleMetadataLayer.CreateNew(1, sourceId, sourceType, payload);
    }

    #endregion

    [Fact]
    public void Rematerialize_NoLayers_AllFieldsNull()
    {
        var title = CreateTitle();

        title.Rematerialize(["igdb"]);

        title.Description.ShouldBeNull();
        title.Publisher.ShouldBeNull();
        title.Developer.ShouldBeNull();
        title.Genre.ShouldBeNull();
        title.ReleaseDate.ShouldBeNull();
        title.Players.ShouldBeNull();
        title.Rating.ShouldBeNull();
        title.FieldProvenance.ShouldBeEmpty();
    }

    [Fact]
    public void Rematerialize_SingleProvider_FieldsPopulated()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "Platform adventure",
            Publisher = "Nintendo",
            Developer = "Nintendo EAD",
            Genre = "Platformer",
            ReleaseDate = new DateOnly(1990, 11, 21),
            Players = 2,
            Rating = 95.0
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.Description.ShouldBe("Platform adventure");
        title.Publisher.ShouldBe("Nintendo");
        title.Developer.ShouldBe("Nintendo EAD");
        title.Genre.ShouldBe("Platformer");
        title.ReleaseDate.ShouldBe(new DateOnly(1990, 11, 21));
        title.Players.ShouldBe(2);
        title.Rating.ShouldBe(95.0);

        title.FieldProvenance.Count.ShouldBe(7);
        title.FieldProvenance.Values.ShouldAllBe(v => v == "igdb");
    }

    [Fact]
    public void Rematerialize_NameExcluded_NameUnchanged()
    {
        // Name comes from DAT data and should NOT be overwritten by providers
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Name = "Super Mario World (IGDB Name)",
            Description = "A game"
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.Name.ShouldBe("Super Mario World"); // Original name preserved
        title.Description.ShouldBe("A game");
        title.FieldProvenance.ShouldNotContainKey("Name");
    }

    [Fact]
    public void Rematerialize_UserLayerWins_OverProvider()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB description",
            Publisher = "IGDB publisher"
        });
        var userLayer = CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
        {
            Description = "User description"
        });

        var title = CreateTitle(layers: [igdbLayer, userLayer]);

        title.Rematerialize(["igdb"]);

        title.Description.ShouldBe("User description");
        title.Publisher.ShouldBe("IGDB publisher"); // Falls through to provider
        title.FieldProvenance["Description"].ShouldBe("user");
        title.FieldProvenance["Publisher"].ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_TitleOverride_WinsOverEverything()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc",
            Publisher = "IGDB pub"
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "SS desc",
            Publisher = "SS pub"
        });
        var userLayer = CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
        {
            Description = "User desc"
        });

        var overrides = new Dictionary<string, string>
        {
            ["Description"] = "screenscraper"  // Force Description from screenscraper
        };

        var title = CreateTitle(layers: [igdbLayer, ssLayer, userLayer], fieldSourceOverrides: overrides);

        title.Rematerialize(["igdb", "screenscraper"]);

        // Override wins over user layer
        title.Description.ShouldBe("SS desc");
        // Publisher not overridden — user layer doesn't have it, falls to provider priority
        title.Publisher.ShouldBe("IGDB pub");
        title.FieldProvenance["Description"].ShouldBe("screenscraper");
        title.FieldProvenance["Publisher"].ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_PlatformDefaults_CascadeStep2()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc",
            Genre = "Platformer"
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "SS desc",
            Genre = "Action"
        });

        var platformDefaults = new Dictionary<string, string>
        {
            ["Genre"] = "screenscraper"  // Platform prefers screenscraper for Genre
        };

        var title = CreateTitle(layers: [igdbLayer, ssLayer]);

        title.Rematerialize(["igdb", "screenscraper"], platformDefaults);

        // Description follows global priority (igdb first)
        title.Description.ShouldBe("IGDB desc");
        // Genre follows platform default (screenscraper)
        title.Genre.ShouldBe("Action");
        title.FieldProvenance["Description"].ShouldBe("igdb");
        title.FieldProvenance["Genre"].ShouldBe("screenscraper");
    }

    [Fact]
    public void Rematerialize_FullCascade_AllFourLevels()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc",
            Publisher = "IGDB pub",
            Developer = "IGDB dev",
            Genre = "Platformer"
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "SS desc",
            Publisher = "SS pub",
            Developer = "SS dev",
            Genre = "Action"
        });
        var userLayer = CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
        {
            Developer = "User dev"
        });

        var overrides = new Dictionary<string, string>
        {
            ["Description"] = "screenscraper"  // Level 1: title override
        };
        var platformDefaults = new Dictionary<string, string>
        {
            ["Genre"] = "screenscraper"  // Level 2: platform default
        };

        var title = CreateTitle(layers: [igdbLayer, ssLayer, userLayer], fieldSourceOverrides: overrides);

        title.Rematerialize(["igdb", "screenscraper"], platformDefaults);

        // Level 1: Title override — Description from screenscraper
        title.Description.ShouldBe("SS desc");
        title.FieldProvenance["Description"].ShouldBe("screenscraper");

        // Level 2: User layer — Developer from user (no override for Developer)
        title.Developer.ShouldBe("User dev");
        title.FieldProvenance["Developer"].ShouldBe("user");

        // Level 3: Platform default — Genre from screenscraper
        title.Genre.ShouldBe("Action");
        title.FieldProvenance["Genre"].ShouldBe("screenscraper");

        // Level 4: Global priority — Publisher from igdb (first in priority)
        title.Publisher.ShouldBe("IGDB pub");
        title.FieldProvenance["Publisher"].ShouldBe("igdb");

    }

    [Fact]
    public void Rematerialize_GlobalPriorityOrder_RespectedForProviders()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc"
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "SS desc"
        });

        var title = CreateTitle(layers: [igdbLayer, ssLayer]);

        // screenscraper first in priority
        title.Rematerialize(["screenscraper", "igdb"]);

        title.Description.ShouldBe("SS desc");
        title.FieldProvenance["Description"].ShouldBe("screenscraper");
    }

    [Fact]
    public void Rematerialize_Override_InvalidSource_FallsThrough()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc"
        });

        var overrides = new Dictionary<string, string>
        {
            ["Description"] = "nonexistent"  // Source doesn't exist
        };

        var title = CreateTitle(layers: [igdbLayer], fieldSourceOverrides: overrides);

        title.Rematerialize(["igdb"]);

        // Falls through to user (none) → platform (none) → global priority
        title.Description.ShouldBe("IGDB desc");
        title.FieldProvenance["Description"].ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_Override_SourceHasNullField_FallsThrough()
    {
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc"
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            // Description is null for this provider
            Publisher = "SS pub"
        });

        var overrides = new Dictionary<string, string>
        {
            ["Description"] = "screenscraper"  // SS has no description
        };

        var title = CreateTitle(layers: [igdbLayer, ssLayer], fieldSourceOverrides: overrides);

        title.Rematerialize(["igdb", "screenscraper"]);

        // Override source has null, falls through to user (none) → global priority
        title.Description.ShouldBe("IGDB desc");
        title.FieldProvenance["Description"].ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_ResetsPreviousValues()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "A description"
        });

        var title = CreateTitle(layers: [layer]);

        // First materialize
        title.Rematerialize(["igdb"]);
        title.Description.ShouldBe("A description");

        // Now store an empty layer (simulate provider returning no data on refresh)
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload());

        // Re-materialize — description should be cleared
        title.Rematerialize(["igdb"]);
        title.Description.ShouldBeNull();
    }

    [Fact]
    public void Rematerialize_ValueTypes_ResolvedCorrectly()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ReleaseDate = new DateOnly(1991, 4, 2),
            Players = 4,
            Rating = 88.5
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.ReleaseDate.ShouldBe(new DateOnly(1991, 4, 2));
        title.Players.ShouldBe(4);
        title.Rating.ShouldBe(88.5);
        title.FieldProvenance["ReleaseDate"].ShouldBe("igdb");
        title.FieldProvenance["Players"].ShouldBe("igdb");
        title.FieldProvenance["Rating"].ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_MixedResolution_EachFieldIndependent()
    {
        // igdb has Description + Genre, screenscraper has Publisher + Rating
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc",
            Genre = "Platformer"
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Publisher = "SS pub",
            Rating = 92.0
        });

        var title = CreateTitle(layers: [igdbLayer, ssLayer]);

        title.Rematerialize(["igdb", "screenscraper"]);

        // Each field comes from whichever provider has it
        title.Description.ShouldBe("IGDB desc");
        title.Genre.ShouldBe("Platformer");
        title.Publisher.ShouldBe("SS pub");
        title.Rating.ShouldBe(92.0);

        title.FieldProvenance["Description"].ShouldBe("igdb");
        title.FieldProvenance["Genre"].ShouldBe("igdb");
        title.FieldProvenance["Publisher"].ShouldBe("screenscraper");
        title.FieldProvenance["Rating"].ShouldBe("screenscraper");
    }

    [Fact]
    public void Rematerialize_UnknownProviderNotInPriority_StillUsedAsFallback()
    {
        // Provider not in global priority list still provides data as last resort
        var unknownLayer = CreateLayer("custom_provider", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "Custom desc"
        });

        var title = CreateTitle(layers: [unknownLayer]);

        title.Rematerialize(["igdb"]); // custom_provider not in priority list

        // Falls through to "first available"
        title.Description.ShouldBe("Custom desc");
        title.FieldProvenance["Description"].ShouldBe("custom_provider");
    }

    [Fact]
    public void SetFieldSourceOverride_And_Clear_WorkCorrectly()
    {
        var title = CreateTitle();

        title.SetFieldSourceOverride("Description", "screenscraper");
        title.FieldSourceOverrides.ShouldContainKeyAndValue("Description", "screenscraper");

        title.ClearFieldSourceOverride("Description");
        title.FieldSourceOverrides.ShouldNotContainKey("Description");
    }

    [Fact]
    public void Rematerialize_BackwardCompatible_NoOverridesNoPlatformDefaults()
    {
        // With no field overrides and no platform defaults, Rematerialize should
        // produce identical output to the old RecalculateEffectiveState behavior:
        // user layer wins, then providers in priority order
        var igdbLayer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc",
            Publisher = "IGDB pub",
            Developer = "IGDB dev",
            Genre = "Platformer",
            ReleaseDate = new DateOnly(1990, 11, 21),
            Players = 2,
            Rating = 95.0
        });
        var ssLayer = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "SS desc",
            Publisher = "SS pub",
            Developer = "SS dev",
            Genre = "Action"
        });
        var userLayer = CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
        {
            Developer = "Custom dev"
        });

        // No overrides — just layers
        var title = CreateTitle(layers: [igdbLayer, ssLayer, userLayer]);

        // Call Rematerialize with no platform defaults (backward compat path)
        title.Rematerialize(["igdb", "screenscraper"]);

        // User layer wins for Developer
        title.Developer.ShouldBe("Custom dev");
        title.FieldProvenance["Developer"].ShouldBe("user");

        // Global priority (igdb first) wins for all other fields
        title.Description.ShouldBe("IGDB desc");
        title.Publisher.ShouldBe("IGDB pub");
        title.Genre.ShouldBe("Platformer");
        title.ReleaseDate.ShouldBe(new DateOnly(1990, 11, 21));
        title.Players.ShouldBe(2);
        title.Rating.ShouldBe(95.0);

        title.FieldProvenance["Description"].ShouldBe("igdb");
        title.FieldProvenance["Publisher"].ShouldBe("igdb");
        title.FieldProvenance["Genre"].ShouldBe("igdb");

        // Calling Rematerialize again with same inputs should be idempotent
        var title2 = CreateTitle(layers: [
            CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
            {
                Description = "IGDB desc",
                Publisher = "IGDB pub",
                Developer = "IGDB dev",
                Genre = "Platformer",
                ReleaseDate = new DateOnly(1990, 11, 21),
                Players = 2,
                Rating = 95.0
            }),
            CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
            {
                Description = "SS desc",
                Publisher = "SS pub",
                Developer = "SS dev",
                Genre = "Action"
            }),
            CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
            {
                Developer = "Custom dev"
            })
        ]);

        title2.Rematerialize(["igdb", "screenscraper"]);

        title2.Description.ShouldBe(title.Description);
        title2.Publisher.ShouldBe(title.Publisher);
        title2.Developer.ShouldBe(title.Developer);
        title2.Genre.ShouldBe(title.Genre);
        title2.ReleaseDate.ShouldBe(title.ReleaseDate);
        title2.Players.ShouldBe(title.Players);
        title2.Rating.ShouldBe(title.Rating);
    }

    [Fact]
    public void StoreProviderLayer_DoesNotRematerialize()
    {
        var title = CreateTitle();

        title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            Description = "IGDB desc"
        });

        // Layer is stored
        title.MetadataLayers.Count.ShouldBe(1);

        // But effective fields are NOT updated (no rematerialization)
        title.Description.ShouldBeNull();
        title.FieldProvenance.ShouldBeEmpty();
    }

    [Fact]
    public void StoreUserLayer_DoesNotRematerialize()
    {
        var title = CreateTitle();

        title.StoreUserLayer(new TitleMetadataPayload
        {
            Publisher = "Custom publisher"
        });

        // Layer is stored
        title.MetadataLayers.Count.ShouldBe(1);
        title.MetadataLayers.First().SourceId.ShouldBe("user");

        // But effective fields are NOT updated
        title.Publisher.ShouldBeNull();
        title.FieldProvenance.ShouldBeEmpty();
    }
}
