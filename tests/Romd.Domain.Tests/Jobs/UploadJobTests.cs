using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class UploadJobTests
{
    [Fact]
    public void Create_DefaultsToCollectionImport()
    {
        var job = UploadJob.Create("library.zip");

        job.ArchiveOnly.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetArchiveOnly_StoresChoice(bool archiveOnly)
    {
        var job = UploadJob.Create("library.zip");

        job.SetArchiveOnly(archiveOnly);

        job.ArchiveOnly.ShouldBe(archiveOnly);
    }
}
