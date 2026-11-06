using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class UploadOutcomeIdentityTests
{
    [Fact]
    public void UploadIdentity_VersionOne_IsStableAndSeparatesJobKindAndExactPath()
    {
        var job = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var id = JobItem.UploadIdentity(job, JobItemKind.Rom, "folder/ü.rom");
        id.ShouldBe(Guid.Parse("be69009c-9799-87d7-ab69-eca985b3a11b"));
        JobItem.UploadIdentity(job, JobItemKind.Dat, "folder/ü.rom").ShouldNotBe(id);
        JobItem.UploadIdentity(Guid.Empty, JobItemKind.Rom, "folder/ü.rom").ShouldNotBe(id);
        JobItem.UploadIdentity(job, JobItemKind.Rom, "Folder/ü.rom").ShouldNotBe(id);
        JobItem.UploadIdentity(job, JobItemKind.Rom, "other/ü.rom").ShouldNotBe(id);
        var first = JobItem.ForRom(job, "folder/ü.rom", 12, RomIngestOutcome.Ingested,
            7, [4], 3, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity();
        var replay = JobItem.ForRom(job, "folder/ü.rom", 12, RomIngestOutcome.Deduplicated,
            7, [4], 3, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity();
        first.Id.ShouldBe(id);
        replay.Id.ShouldBe(id); // Result classification must never alter the member's key.
        first.Outcome.ShouldBe(JobItemOutcome.Ingested);
        first.MatchedTitleIds.ShouldBe(new[] { 4 });
    }
}
