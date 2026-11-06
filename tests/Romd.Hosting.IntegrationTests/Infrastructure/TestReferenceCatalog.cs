using NSubstitute;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal static class TestReferenceCatalog
{
    internal static IReferenceCatalogService Create()
    {
        var service = Substitute.For<IReferenceCatalogService>();
        service.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(
            new SystemKeys(new Dictionary<int, string> { [1] = "snes", [2] = "psx", [5] = "nes" }));
        return service;
    }
}
