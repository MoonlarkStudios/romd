using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Systems;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Export;

public sealed class ExportJobFormatMappingTests
{
    [Theory]
    [InlineData(RomDeliveryFormat.Original, "original")]
    [InlineData(RomDeliveryFormat.Rvz, "rvz")]
    [InlineData(RomDeliveryFormat.Chd, "chd")]
    public void ToContract_ExportFormat_UsesStableLowercaseId(RomDeliveryFormat format, string wire)
    {
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: format);
        job.ToContract(new SystemKeys(new Dictionary<int, string>())).Format.ShouldBe(wire);
    }
}
