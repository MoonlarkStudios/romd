using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Queries.GetPlatformBios;
using Romd.Admin.Application.Source.Platform;
using Shouldly;
using Xunit;
using DomainPlatform = Romd.Domain.Source.Platform.Platform;

namespace Romd.Application.Tests.Catalog;

public sealed class GetPlatformBiosQueryHandlerTests
{
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();
    private readonly IBiosRepository _biosRepository = Substitute.For<IBiosRepository>();

    [Fact]
    public async Task HandleAsync_PlatformExists_ReturnsOwnershipList()
    {
        _platformRepository.GetByIdAsync(3, Arg.Any<CancellationToken>())
            .Returns(DomainPlatform.CreateNew("Sony PlayStation", "psx"));
        _biosRepository.GetByPlatformWithOwnershipAsync(3, Arg.Any<CancellationToken>())
            .Returns(new List<BiosOwnership>
            {
                new(1, 3, "[BIOS] PSX (USA)", TotalRoms: 1, OwnedRoms: 1,
                    RequiredBytes: 512 * 1024, OwnedBytes: 512 * 1024, OnDiskBytes: 256 * 1024)
            });

        var handler = new GetPlatformBiosQueryHandler(_platformRepository, _biosRepository);

        var result = await handler.HandleAsync(new GetPlatformBiosQuery(3));

        result.IsError.ShouldBeFalse();
        result.Value.Count.ShouldBe(1);
        result.Value[0].IsOwned.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAsync_PlatformMissing_ReturnsError()
    {
        _platformRepository.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((DomainPlatform?)null);

        var handler = new GetPlatformBiosQueryHandler(_platformRepository, _biosRepository);

        var result = await handler.HandleAsync(new GetPlatformBiosQuery(99));

        result.IsError.ShouldBeTrue();
        await _biosRepository.DidNotReceive()
            .GetByPlatformWithOwnershipAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
