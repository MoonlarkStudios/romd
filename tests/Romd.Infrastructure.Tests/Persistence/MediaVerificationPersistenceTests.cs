using Microsoft.EntityFrameworkCore;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MediaVerificationPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData("linux-arm64", -1L)]
    [InlineData("linux-arm64", 1234L)]
    [InlineData("linux-x64", -1L)]
    [InlineData("linux-x64", 1234L)]
    [InlineData("osx-arm64", -1L)]
    [InlineData("osx-arm64", 1234L)]
    [InlineData("win-x64", -1L)]
    [InlineData("win-x64", 1234L)]
    public async Task NativeMeasurement_RoundTrip_RetainsExactTupleHashesAndSubMillisecondInstant(string rid, long ticksAfterEpoch)
    {
        await using var db = _database.CreateContext();
        var fixture = await MediaFactGraphFixture.CreateAsync(db);
        var first = fixture.Cd.Verifications.Single();
        var decoder = new RomChdDecoderBuild(rid, MediaFactGraphFixture.Short('a'), 3);
        var observed = DateTimeOffset.UnixEpoch.AddTicks(ticksAfterEpoch).ToOffset(TimeSpan.FromHours(-5));
        var proof = new RomRepresentationVerification(Guid.NewGuid(), first.ArtifactSha256, first.ArtifactSize,
            new RomDecoderIdentity.Native(decoder), "complete-native-read/v1", "test-verifier/mvid:exact", observed,
            first.Outputs, fixture.Cd.Container, first.ComputedRawSha1, first.ComputedOverallSha1);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await new MediaFactPublisher(db).AppendVerificationAsync(fixture.Cd.Id, proof)).ShouldBeTrue();
            await transaction.CommitAsync();
        }

        await using var observer = _database.CreateContext();
        var retained = (await new MediaFactReader(observer).ReadAsync([fixture.Cd.Id]))[fixture.Cd.Id];
        retained.Verifications.Count.ShouldBe(2);
        retained.Verifications.ShouldContain(measurement => measurement.Id == first.Id);
        var measured = retained.Verifications.Single(measurement => measurement.Id == proof.Id);
        measured.Decoder.ShouldBeOfType<RomDecoderIdentity.Native>().Build.ShouldBe(decoder);
        measured.ArtifactSha256.ShouldBe(proof.ArtifactSha256);
        measured.ArtifactSize.ShouldBe(proof.ArtifactSize);
        measured.ObservedAt.ShouldBe(observed.ToUniversalTime());
        measured.ObservedAt.UtcTicks.ShouldBe(observed.UtcTicks);
        measured.CheckedProfile.ShouldBe(proof.CheckedProfile);
        measured.VerifierBuild.ShouldBe(proof.VerifierBuild);
        measured.ObservedParametersSha256.ShouldBe(fixture.Cd.Container.Fingerprint);
        measured.ComputedRawSha1.ShouldBe(proof.ComputedRawSha1);
        measured.ComputedOverallSha1.ShouldBe(proof.ComputedOverallSha1);
        measured.Outputs.Single().Identity.ShouldBe(first.Outputs.Single().Identity);
        measured.Outputs.Single().Selector.ShouldBe(first.Outputs.Single().Selector);
        var row = await observer.Verifications.SingleAsync(value => value.Id == proof.Id);
        row.RepresentationId.ShouldBe(fixture.Cd.Id);
        row.ObservedAtUtcTicks.ShouldBe(observed.UtcTicks);
        row.DecoderRid.ShouldBe(rid);
        row.DecoderUpstreamCommit.ShouldBe(decoder.UpstreamCommit);
        row.DecoderRecipeVersion.ShouldBe(decoder.RecipeVersion);
        row.DecoderBuild.ShouldBe(decoder.BuildLabel);
    }
}
