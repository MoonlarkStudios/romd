using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public class UploadJobEntityTests
{
    [Fact]
    public void FromDomain_ToDomain_RoundTripsUploadChoices()
    {
        var domain = UploadJob.Create("library.zip", platformId: 7);
        domain.SetMaxParallelRoms(8);
        domain.SetAllowUnidentified(true);
        domain.SetArchiveOnly(true);

        var entity = UploadJobEntity.FromDomain(domain);
        entity.AllowUnidentified.ShouldBeTrue();
        entity.ArchiveOnly.ShouldBeTrue();
        entity.MaxParallelRoms.ShouldBe(8);
        entity.JobType.ShouldBe("upload");

        var roundTripped = entity.ToDomain();
        roundTripped.AllowUnidentified.ShouldBeTrue();
        roundTripped.ArchiveOnly.ShouldBeTrue();
        roundTripped.MaxParallelRoms.ShouldBe(8);
        roundTripped.PlatformId.ShouldBe(7);
    }

    [Fact]
    public void Create_DefaultsUploadChoicesToFalse()
    {
        var domain = UploadJob.Create("library.zip");

        domain.AllowUnidentified.ShouldBeFalse();
        domain.ArchiveOnly.ShouldBeFalse();
        UploadJobEntity.FromDomain(domain).ToDomain().AllowUnidentified.ShouldBeFalse();
        UploadJobEntity.FromDomain(domain).ToDomain().ArchiveOnly.ShouldBeFalse();
    }

    [Fact]
    public void FromDomain_ToDomain_RoundTripsImportSource()
    {
        var domain = UploadJob.Create("roms-folder/");
        domain.SetImportSource("/mnt/nas/roms-folder", move: true);

        var entity = UploadJobEntity.FromDomain(domain);
        entity.ImportSourcePath.ShouldBe("/mnt/nas/roms-folder");
        entity.ImportMove.ShouldBeTrue();

        var roundTripped = entity.ToDomain();
        roundTripped.ImportSourcePath.ShouldBe("/mnt/nas/roms-folder");
        roundTripped.ImportMove.ShouldBeTrue();
    }

    [Fact]
    public void Create_DefaultsImportSourceToNone()
    {
        var domain = UploadJob.Create("library.zip");

        domain.ImportSourcePath.ShouldBeNull();
        domain.ImportMove.ShouldBeFalse();
        var roundTripped = UploadJobEntity.FromDomain(domain).ToDomain();
        roundTripped.ImportSourcePath.ShouldBeNull();
        roundTripped.ImportMove.ShouldBeFalse();
    }
}
