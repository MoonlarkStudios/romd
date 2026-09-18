using NSubstitute;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;

namespace Romd.Application.Tests;

internal static class TestSystemCatalog
{
    // Relational fixture IDs deliberately differ from public reference keys.
    internal static readonly SystemKeys Keys = new(new Dictionary<int, string>
    {
        [1] = "nes", [2] = "snes", [3] = "gba", [7] = "n64",
        [9] = "arcade", [23] = "local-fixture", [10] = "local-test", [42] = "local-other", [100] = "local-hundred"
    });

    internal static IReferenceCatalogService Create()
    {
        var catalog = Substitute.For<IReferenceCatalogService>();
        catalog.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(Keys);
        return catalog;
    }
}
