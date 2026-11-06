using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Application.Common.Concurrency;
using Romd.Admin.Application.MetadataProviders.Commands.TestIgdbConnection;
using Romd.Persistence;
using Romd.Admin.Application.MetadataProviders;
using Romd.Infrastructure.Enrichment;
using Romd.Persistence.MetadataProviders;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class S6IgdbSettingsRevisionRegressionTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task TestConnectionAsync_UnconfiguredSettings_RotatesRevisionWithStoredTestStatus()
    {
        await using var db = _database.CreateContext();
        var httpClients = Substitute.For<IHttpClientFactory>();
        IIgdbProviderSettingsService service = new IgdbProviderSettingsService(new IgdbProviderSettingsStore(db),
            new EphemeralDataProtectionProvider(), Options.Create(new IgdbProviderOptions()),
            Options.Create(new EnrichmentOptions()), httpClients,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero)));
        var before = await service.GetAsync();

        var result = await new TestIgdbConnectionCommandHandler(service, new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TestIgdbConnectionCommandHandler>.Instance)
            .HandleAsync(new(RevisionPrecondition.For(before.Revision.ToString())));
        result.IsError.ShouldBeFalse();
        var tested = result.Value;

        tested.LastTestSucceeded.ShouldBe(false);
        tested.LastTestedAt.ShouldNotBeNull();
        tested.Revision.ShouldNotBe(before.Revision, "The validator must change when the persisted test representation changes.");
        var stored = await service.GetAsync();
        stored.Revision.ShouldBe(tested.Revision);
        stored.LastTestedAt.ShouldBe(tested.LastTestedAt);
        httpClients.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    public void Dispose() => _database.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
