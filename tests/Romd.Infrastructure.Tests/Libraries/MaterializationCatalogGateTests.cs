using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed class MaterializationCatalogGateTests
{
    [Fact]
    public void IsBlocked_NoPlatformsNeedRebuild_NeverBlocks()
    {
        var unrestricted = new LibraryConfiguration();

        MaterializationCatalogGate.IsBlocked(unrestricted, []).ShouldBeFalse();
    }

    [Fact]
    public void IsBlocked_UnrestrictedScope_BlocksWhileAnyPlatformNeedsRebuild()
    {
        var unrestricted = new LibraryConfiguration();

        MaterializationCatalogGate.IsBlocked(unrestricted, [3]).ShouldBeTrue();
    }

    [Fact]
    public void IsBlocked_ScopedToCleanPlatform_DoesNotBlock()
    {
        var scoped = new LibraryConfiguration { AllowedPlatformIds = [5] };

        MaterializationCatalogGate.IsBlocked(scoped, [3]).ShouldBeFalse();
    }

    [Fact]
    public void IsBlocked_ScopedToRebuildingPlatform_Blocks()
    {
        var scoped = new LibraryConfiguration { AllowedPlatformIds = [3, 5] };

        MaterializationCatalogGate.IsBlocked(scoped, [3]).ShouldBeTrue();
    }
}
