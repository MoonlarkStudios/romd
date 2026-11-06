using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Ids;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class OriginalDiscLayoutTests
{
    private const string _descriptorText = "FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n";

    [Fact]
    public async Task Confirm_CatalogRequiresRetainedDescriptor_AcceptsWithoutDuplicatingEntryPoint()
    {
        await AddDescriptorRequirementAsync("measured");
        var pending = await Intake();

        var accepted = await Confirm(pending);

        accepted.State.ShouldBe("accepted");
        await using var db = database.CreateContext();
        var bindings = await db.OriginalDiscLayoutFiles.Where(row => row.OriginalDiscLayoutId == Decode(accepted.Id)).ToListAsync();
        bindings.Count.ShouldBe(1);
        bindings.Single().Path.ShouldBe("track.bin");
        (await db.RomFiles.CountAsync()).ShouldBe(1); // The descriptor needs no separate logical ROM owner.
        (await new FileRepository(db).IsReferencedAsync(2, default)).ShouldBeTrue();
        (await new FileRepository(db).GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.MaxValue, default)).ShouldNotContain(2);
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var resolved = await new ConsumerOriginalDiscLayoutReader(db).ResolveAsync(1, 1, "sha1:tracks", default);
        resolved.ShouldNotBeNull();
        resolved.Files.Count.ShouldBe(2);
        resolved.Files.Count(file => file.Role == "descriptor").ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_CatalogRequiresRetainedDescriptor_UsesIndependentMeasurement()
    {
        // Construct the accepted snapshot independently of the confirmation regression above.
        var accepted = await Confirm(await Intake());
        await AddDescriptorRequirementAsync("measured");
        await using var db = database.CreateContext();
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        var resolved = await new ConsumerOriginalDiscLayoutReader(db).ResolveAsync(1, 1, "sha1:tracks", default);

        resolved.ShouldNotBeNull();
        resolved.Id.ShouldBe(Decode(accepted.Id));
        resolved.Files.Count.ShouldBe(2);
        (await db.RomFiles.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-size")]
    [InlineData("wrong-sha1")]
    [InlineData("other-file")]
    [InlineData("missing-catalog-sha1")]
    public async Task Confirm_DescriptorDoesNotProveRequirement_RefusesWithoutPublishingDecision(string evidence)
    {
        await AddDescriptorRequirementAsync(evidence);
        var pending = await Intake(file: evidence == "other-file" ? 3 : 2);
        await using var db = database.CreateContext();
        var beforeAudit = await db.Set<OriginalDiscLayoutAuditEntity>().CountAsync();

        var result = await DecideAsync(db, Decode(pending.Id), new(pending.Revision, "confirm",
            IdCoder.Encode(1), [new("track.bin", IdCoder.Encode(1))], "Reviewed"), actor, default);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("OriginalLayout.Incomplete");
        var retained = await db.OriginalDiscLayouts.SingleAsync();
        retained.Revision.ShouldBe(pending.Revision);
        retained.State.ShouldBe("pending");
        (await db.Set<OriginalDiscLayoutAuditEntity>().CountAsync()).ShouldBe(beforeAudit);
        (await db.OriginalDiscLayoutFiles.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-size")]
    [InlineData("wrong-sha1")]
    [InlineData("other-file")]
    [InlineData("missing-catalog-sha1")]
    public async Task Resolve_DescriptorDoesNotProveRequirement_RefusesEvenWhenAnotherFileIsHeld(string evidence)
    {
        var accepted = await Confirm(await Intake(file: evidence == "other-file" ? 3 : 2));
        await AddDescriptorRequirementAsync(evidence);
        await using var db = database.CreateContext();
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        var resolved = await new ConsumerOriginalDiscLayoutReader(db).ResolveAsync(1, 1, "sha1:tracks", default);

        resolved.ShouldBeNull();
        (await db.OriginalDiscLayouts.SingleAsync()).Revision.ShouldBe(accepted.Revision);
        (await new FileRepository(db).IsReferencedAsync(evidence == "other-file" ? 3 : 2, default)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("measured", true)]
    [InlineData("missing", false)]
    [InlineData("wrong-size", false)]
    [InlineData("wrong-sha1", false)]
    [InlineData("other-file", false)]
    [InlineData("missing-catalog-sha1", false)]
    public async Task Candidates_ReportsExactDescriptorCoverageWithoutASeparateRom(string evidence, bool expected)
    {
        await AddDescriptorRequirementAsync(evidence);
        var pending = await Intake(file: evidence == "other-file" ? 3 : 2);
        await using var db = database.CreateContext();
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));

        var candidates = await service.CandidatesAsync(Decode(pending.Id), 1, null, 100, default);

        candidates.IsError.ShouldBeFalse();
        var descriptor = candidates.Value.Items.Single().Requirements.Single(requirement => requirement.Name == "catalog-descriptor.cue");
        descriptor.SatisfiedByDescriptor.ShouldBe(expected);
        descriptor.OriginalAvailable.ShouldBeFalse();
        (await db.RomFiles.CountAsync()).ShouldBe(1);
    }

    private async Task AddDescriptorRequirementAsync(string evidence)
    {
        byte[] descriptor = Encoding.UTF8.GetBytes(_descriptorText);
        byte[] other = Encoding.UTF8.GetBytes("REM repair\n" + _descriptorText);
        await using var db = database.CreateContext();
        if (evidence != "missing")
        {
            var md5 = Md5.FromBytes(MD5.HashData(descriptor));
            var sha1 = Sha1.FromBytes(SHA1.HashData(descriptor));
            await db.Files.Where(file => file.Id == 2).ExecuteUpdateAsync(update => update
                .SetProperty(file => file.ArtifactMd5, md5)
                .SetProperty(file => file.ArtifactSha1, sha1));
        }
        if (evidence == "other-file")
        {
            var md5 = Md5.FromBytes(MD5.HashData(other));
            var sha1 = Sha1.FromBytes(SHA1.HashData(other));
            await db.Files.Where(file => file.Id == 3).ExecuteUpdateAsync(update => update
                .SetProperty(file => file.ArtifactMd5, md5)
                .SetProperty(file => file.ArtifactSha1, sha1));
        }
        db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity
        {
            CatalogReleaseId = 1,
            FileFingerprint = "sha1:descriptor",
            Name = "catalog-descriptor.cue", // Coverage follows measured bytes, not the entry-point name.
            Size = descriptor.Length + (evidence == "wrong-size" ? 1 : 0),
            Sha1 = evidence == "missing-catalog-sha1" ? null : Sha1.FromBytes(SHA1.HashData(evidence == "wrong-sha1" ? other : descriptor)),
        });
        await db.SaveChangesAsync();
    }
}
