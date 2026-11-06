using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class TitleSnapshotReadEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Enrichment_StableTitle_PreservesPayloadStrongETagAndNonRevalidation()
    {
        var (id, revision, _) = await SeedAsync();
        using var client = fixture.CreateAuthenticatedClient();
        var path = $"/api/titles/{IdCoder.Encode(id)}/enrichment";

        using var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.ETag.ShouldNotBeNull().IsWeak.ShouldBeFalse();
        response.Headers.ETag.Tag.ShouldBe($"\"{revision}\"");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("titleId").GetString().ShouldBe(IdCoder.Encode(id));
        body.RootElement.GetProperty("revision").GetString().ShouldBe(revision.ToString());
        body.RootElement.GetProperty("layers")[0].GetProperty("description").GetString().ShouldBe("Provider description");
        body.RootElement.GetProperty("externalIds")[0].GetProperty("externalId").GetString().ShouldBe("42");
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var repeated = await client.SendAsync(conditional);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeated.Headers.ETag.ShouldBe(response.Headers.ETag);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("MERGE")]
    public async Task InitialAggregateRead_SustainedRevisionChurn_Returns409AfterBoundedRepositoryRetries(string operation)
    {
        var (targetId, _, connection) = await SeedAsync();
        int sourceId = operation == "MERGE" ? (await SeedAsync()).Id : targetId;
        var changingId = operation == "MERGE" ? sourceId : targetId;
        var churn = new RevisionChurnInterceptor(async ct =>
        {
            await using var writer = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection).UseOpenIddict().Options);
            await writer.Titles.Where(title => title.Id == changingId)
                .ExecuteUpdateAsync(update => update.SetProperty(title => title.Revision, Guid.NewGuid()), ct);
        });
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITitleAggregateReader>();
            services.AddScoped<ITitleAggregateReader>(_ => new OwnedTitleReader(new RomdDbContext(
                new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(connection).UseOpenIddict()
                    .AddInterceptors(churn).Options)));
        }));
        using var client = host.CreateClient().WithTestUser(Guid.NewGuid(), "admin@localhost", [RomdRoleType.Admin]);
        string title = $"/api/titles/{IdCoder.Encode(targetId)}";
        using var request = operation switch
        {
            "GET" => new HttpRequestMessage(HttpMethod.Get, title + "/enrichment"),
            "PATCH" => new HttpRequestMessage(HttpMethod.Patch, title + $"/media/{IdCoder.Encode(1)}")
            { Content = JsonContent.Create(new { isPrimary = true }) },
            "DELETE" => new HttpRequestMessage(HttpMethod.Delete, title + $"/media/{IdCoder.Encode(1)}"),
            _ => new HttpRequestMessage(HttpMethod.Post, title + "/merge")
            {
                Content = JsonContent.Create(new
                {
                    sourceTitleId = IdCoder.Encode(sourceId),
                    sourceTitleRevision = Guid.NewGuid().ToString()
                })
            }
        };
        if (operation != "GET")
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{Guid.NewGuid()}\"");

        using var response = await client.SendAsync(request);

        using var problem = await ProblemEnvelope.ReadAsync(response, 409, "Titles.ReadConflict");
        response.Headers.ETag.ShouldBeNull();
        churn.Mutations.ShouldBe(3, "the real repository must exhaust exactly its bounded read retries");
        var detail = problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull();
        detail.ShouldNotContain(nameof(PersistenceConflictException));
        detail.Any(char.IsDigit).ShouldBeFalse("internal title ids must not enter the conflict response");
    }

    private async Task<(int Id, Guid Revision, string Connection)> SeedAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int platform = await db.Platforms.OrderBy(system => system.Id).Select(system => system.Id).FirstAsync();
        var title = new TitleEntity
        {
            PlatformId = platform,
            Name = "Read test",
            NormalizedName = Guid.NewGuid().ToString("N"),
            Revision = Guid.NewGuid(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        db.TitleMetadataLayers.Add(TitleMetadataLayerEntity.FromDomain(TitleMetadataLayer.CreateNew(title.Id,
            "provider", MetadataSourceType.Provider, new() { Description = "Provider description" }, updatedAt: DateTimeOffset.UnixEpoch)));
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(title.Id, "provider", "42", .9f, createdAt: DateTimeOffset.UnixEpoch)));
        await db.SaveChangesAsync();
        return (title.Id, title.Revision, db.Database.GetConnectionString()!);
    }

    private sealed class OwnedTitleReader(RomdDbContext context) : ITitleAggregateReader, IAsyncDisposable
    {
        public Task<Title?> GetAggregateAsync(int titleId, CancellationToken cancellationToken = default) =>
            new TitleRepository(context).GetAggregateAsync(titleId, cancellationToken);
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class RevisionChurnInterceptor(Func<CancellationToken, Task> mutate) : DbCommandInterceptor
    {
        public int Mutations { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"TitleExternalIds\""))
            {
                Mutations++;
                await mutate(cancellationToken);
            }
            return result;
        }
    }
}
