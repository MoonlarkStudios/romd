using NSubstitute;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Libraries;

public sealed class LibraryExperienceQueryTests
{
    [Fact]
    public async Task Preview_PendingMaterialization_DoesNotExposeStaleAudienceResults()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        var experience = Substitute.For<ILibraryExperienceRepository>();
        libraries.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Library.CreateNew("Kids", new LibraryConfiguration()));
        var handler = new GetLibraryPreviewQueryHandler(libraries, experience);

        var result = await handler.HandleAsync(new(7, null, 0, int.MinValue));

        result.IsError.ShouldBeFalse();
        result.Value.Items.ShouldBeEmpty();
        await experience.DidNotReceive().GetPreviewAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_UnknownLibrary_ReturnsNotFound()
    {
        var handler = new GetLibraryPreviewQueryHandler(Substitute.For<ILibraryRepository>(), Substitute.For<ILibraryExperienceRepository>());
        var result = await handler.HandleAsync(new(7, null, 0, int.MinValue));
        result.FirstError.Code.ShouldBe("Libraries.NotFound");
    }
}
