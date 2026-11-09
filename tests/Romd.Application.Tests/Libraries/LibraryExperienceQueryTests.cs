using Romd.Contracts.Common.Models;
using NSubstitute;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

using Library = Romd.Domain.Libraries.Library;
using LibraryConfiguration = Romd.Domain.Libraries.LibraryConfiguration;

namespace Romd.Application.Tests.Libraries;

public sealed class LibraryExperienceQueryTests
{
    [Fact]
    public async Task Preview_NeverPublishedLibrary_ReturnsNoItems()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var experience = Substitute.For<ILibraryExperienceRepository>();
        libraries.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Library.CreateNew("Kids", new LibraryConfiguration(), now: DateTimeOffset.UnixEpoch));
        experience.GetPreviewPageAsync(7, null, null, 48, Arg.Any<CancellationToken>()).Returns(new Page<LibraryPreviewTitle> { Items = [], HasNextPage = false });
        var handler = new GetLibraryPreviewQueryHandler(libraries, experience);

        var result = await handler.HandleAsync(new(7, null));

        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        await experience.DidNotReceive().GetPreviewAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_PublishedLibraryWithPendingChanges_ShowsPublishedSnapshot()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var experience = Substitute.For<ILibraryExperienceRepository>();
        var published = new Page<LibraryPreviewTitle> { Items = [], HasNextPage = false };
        libraries.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Library.Rehydrate(
            7,
            "Kids",
            new LibraryConfiguration(),
            LibraryConfigurationState.Valid,
            configurationError: null,
            isDefault: false,
            needsMaterialization: true,
            lastMaterializedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            itemCount: 3,
            createdAt: DateTimeOffset.UtcNow.AddDays(-1),
            updatedAt: DateTimeOffset.UtcNow));
        experience.GetPreviewPageAsync(7, null, null, 48, Arg.Any<CancellationToken>()).Returns(published);
        var handler = new GetLibraryPreviewQueryHandler(libraries, experience);

        var result = await handler.HandleAsync(new(7, null));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeSameAs(published);
    }

    [Fact]
    public async Task Preview_UnknownLibrary_ReturnsNotFound()
    {
        var handler = new GetLibraryPreviewQueryHandler(Substitute.For<ILibraryRepository>(), Substitute.For<ILibraryExperienceRepository>());
        var result = await handler.HandleAsync(new(7, null));
        result.FirstError.Code.ShouldBe("Libraries.NotFound");
    }
}
