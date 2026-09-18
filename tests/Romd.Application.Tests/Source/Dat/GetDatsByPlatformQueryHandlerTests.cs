using NSubstitute;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Queries.GetDatsByPlatform;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;
using ErrorOr;
using DomainPlatform = Romd.Domain.Source.Platform.Platform;

namespace Romd.Application.Tests.Source.Dat;

public sealed class GetDatsByPlatformQueryHandlerTests
{
    private const int PlatformId = 3;

    private readonly IDatRepository _datRepository = Substitute.For<IDatRepository>();
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();

    private GetDatsByPlatformQueryHandler CreateHandler() => new(_platformRepository, _datRepository);

    private static DatWithSourceStatus CreateDat(
        int id,
        string name,
        CatalogSourceStatus sourceStatus,
        int catalogSourceId) =>
        new(
            DatFile.Rehydrate(
                id,
                name,
                name,
                null,
                null,
                null,
                DatType.NoIntro,
                PlatformId,
                $"{name}.dat",
                id,
                DateTimeOffset.UtcNow,
                null,
                0,
                0,
                0,
                id,
                DatFileLifecycle.Active,
                null),
            sourceStatus,
            catalogSourceId);

    [Fact]
    public async Task HandleAsync_UnknownPlatform_ReturnsPlatformNotFound()
    {
        _platformRepository.GetByIdAsync(PlatformId, Arg.Any<CancellationToken>()).Returns((DomainPlatform?)null);

        var result = await CreateHandler().HandleAsync(new GetDatsByPlatformQuery(PlatformId));

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("Catalog.PlatformNotFound");
    }

    [Fact]
    public async Task HandleAsync_PlatformWithoutDats_ReturnsEmpty()
    {
        _platformRepository.GetByIdAsync(PlatformId, Arg.Any<CancellationToken>())
            .Returns(DomainPlatform.Rehydrate(PlatformId, "Super Nintendo", "snes", null, DateTimeOffset.UtcNow));
        _datRepository.GetByPlatformIdAsync(PlatformId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateHandler().HandleAsync(new GetDatsByPlatformQuery(PlatformId));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_PlatformDats_ReturnsTheRepositorySnapshotUnchanged()
    {
        // The repository rows already carry status and catalog id from one query snapshot;
        // the handler performs no dictionary composition that a concurrent deletion could tear.
        _platformRepository.GetByIdAsync(PlatformId, Arg.Any<CancellationToken>())
            .Returns(DomainPlatform.Rehydrate(PlatformId, "Super Nintendo", "snes", null, DateTimeOffset.UtcNow));
        var snapshot = new[]
        {
            CreateDat(1, "Nintendo - SNES", CatalogSourceStatus.Active, 11),
            CreateDat(2, "Nintendo - SNES (Parent-Clone)", CatalogSourceStatus.Discontinued, 12)
        };
        _datRepository.GetByPlatformIdAsync(PlatformId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var result = await CreateHandler().HandleAsync(new GetDatsByPlatformQuery(PlatformId));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(snapshot);
        result.Value[0].Dat.Name.ShouldBe("Nintendo - SNES");
        result.Value[0].CatalogSourceId.ShouldBe(11);
        result.Value[1].SourceStatus.ShouldBe(CatalogSourceStatus.Discontinued);
        result.Value[1].CatalogSourceId.ShouldBe(12);
    }
}
