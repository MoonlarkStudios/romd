using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Taxonomy;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Taxonomy;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     The admin region and language collections (docs/decisions/api-contract-policy.md, "Resource Model",
///     "Pagination" and "Strict Validation"): every row, registered or minted from a DAT token, paged by keyset
///     cursor and addressed by its natural key, with merge and alias management under the row.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RegionLanguageEndpointsTests(IntegrationTestFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.CreateAuthenticatedClient();

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("regions", "asia")]
    [InlineData("languages", "en")]
    public async Task List_PagesEveryRowByCursor_AndRejectsInvalidPaging(string collection, string builtInKey)
    {
        string unregistered = await AddUnregisteredAsync(collection);
        var keys = new List<string>();
        string? cursor = null;
        do
        {
            var page = (await _client.GetFromJsonAsync<Page<JsonElement>>(
                $"/api/{collection}?limit=7" + (cursor is null ? "" : "&cursor=" + cursor)))!;
            page.Items.Count.ShouldBeLessThanOrEqualTo(7);
            keys.AddRange(page.Items.Select(item => item.GetProperty("key").GetString()!));
            cursor = page.NextCursor;
        } while (cursor is not null);

        keys.ShouldContain(builtInKey);
        keys.ShouldContain(unregistered);
        keys.Distinct().Count().ShouldBe(keys.Count);
        using (var tooLarge = await _client.GetAsync($"/api/{collection}?limit=101"))
        using (var body = await ProblemEnvelope.ReadAsync(tooLarge, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").TryGetProperty("limit", out _).ShouldBeTrue();
        using (var invalid = await _client.GetAsync($"/api/{collection}?cursor=not-a-cursor"))
        using (var body = await ProblemEnvelope.ReadAsync(invalid, 400, "Taxonomy.InvalidCursor"))
            body.RootElement.GetProperty("errors").TryGetProperty("cursor", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task List_CursorFromTheOtherCollection_Returns400()
    {
        var regions = (await _client.GetFromJsonAsync<Page<JsonElement>>("/api/regions?limit=1"))!;
        regions.NextCursor.ShouldNotBeNull();

        using var response = await _client.GetAsync("/api/languages?limit=1&cursor=" + regions.NextCursor);

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Taxonomy.InvalidCursor");
        body.RootElement.GetProperty("errors").TryGetProperty("cursor", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetRegion_ServesTheManagementProjection_AndRevalidatesAgainstItsAliases()
    {
        string key = await AddUnregisteredAsync("regions");
        var response = await _client.GetAsync($"/api/regions/{key}");
        var region = (await response.Content.ReadFromJsonAsync<Region>())!;
        (region.Key, region.Ownership, region.CanMerge, region.IsAutoCreated).ShouldBe((key, null, true, true));
        var registered = (await _client.GetFromJsonAsync<Region>("/api/regions/asia"))!;
        (registered.Ownership, registered.CanMerge).ShouldBe(("Romd", false));
        registered.Aliases.Items.ShouldContain(alias => alias.Ownership == "Romd");

        using (var unchanged = new HttpRequestMessage(HttpMethod.Get, $"/api/regions/{key}"))
        {
            unchanged.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            (await _client.SendAsync(unchanged)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        }
        (await _client.PostAsJsonAsync($"/api/regions/{key}/aliases", new { alias = "Revalidated " + key })).EnsureSuccessStatusCode();
        using (var changed = new HttpRequestMessage(HttpMethod.Get, $"/api/regions/{key}"))
        {
            changed.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            (await _client.SendAsync(changed)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        using var missing = await _client.GetAsync("/api/regions/dat-no-such-region");
        using var _ = await ProblemEnvelope.ReadAsync(missing, 404, "ReferenceResource.NotFound");
    }

    [Theory]
    [InlineData("regions", "asia")]
    [InlineData("languages", "en")]
    public async Task Alias_CreateListDeleteRoundTrip_IsKeyedWithSqidAliasIds(string collection, string key)
    {
        string aliasText = $"{collection}-alias-{Guid.NewGuid():N}";

        var created = await _client.PostAsJsonAsync($"/api/{collection}/{key}/aliases", new { alias = "  " + aliasText.ToUpperInvariant() });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var alias = (await created.Content.ReadFromJsonAsync<AliasResource>())!;
        (alias.Alias, alias.Ownership).ShouldBe((aliasText, "Installation"));
        IdCoder.TryDecode(alias.Id, out _).ShouldBeTrue();
        created.Headers.Location!.ToString().ShouldBe($"/api/{collection}/{key}/aliases/{alias.Id}");
        (await ReadAliasesAsync(collection, key)).ShouldContain(item => item.Id == alias.Id);
        using (var duplicate = await _client.PostAsJsonAsync($"/api/{collection}/{key}/aliases", new { alias = aliasText }))
        using (var _ = await ProblemEnvelope.ReadAsync(duplicate, 409, "Taxonomy.AliasAlreadyExists"))
        { }

        (await _client.DeleteAsync($"/api/{collection}/{key}/aliases/{alias.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (var again = await _client.DeleteAsync($"/api/{collection}/{key}/aliases/{alias.Id}"))
        using (var _ = await ProblemEnvelope.ReadAsync(again, 404, "Taxonomy.AliasNotFound"))
        { }
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("languages")]
    public async Task Alias_InvalidTextOrUnknownRow_IsRefusedBeforeAnyWrite(string collection)
    {
        using (var tooLong = await _client.PostAsJsonAsync($"/api/{collection}/dat-no-such-row/aliases", new { alias = new string('a', 101) }))
        using (var body = await ProblemEnvelope.ReadAsync(tooLong, 400, "Taxonomy.InvalidAlias"))
            body.RootElement.GetProperty("errors").GetProperty("alias").GetArrayLength().ShouldBe(1);
        using (var unknown = await _client.PostAsJsonAsync($"/api/{collection}/dat-no-such-row/aliases", new { alias = "fine" }))
        using (var _ = await ProblemEnvelope.ReadAsync(unknown, 404, "ReferenceResource.NotFound"))
        { }
        using (var malformed = await _client.DeleteAsync($"/api/{collection}/dat-no-such-row/aliases/not-a-sqid"))
        using (var body = await ProblemEnvelope.ReadAsync(malformed, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").TryGetProperty("aliasId", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("regions", "asia")]
    [InlineData("languages", "en")]
    public async Task Merge_PreviewAndActionShareTheirVerdicts_AndMoveTheSourceIntoTheTarget(string collection, string registeredKey)
    {
        string source = await AddUnregisteredAsync(collection, "merge-source");
        string target = await AddUnregisteredAsync(collection, "merge-target");
        (await _client.PostAsJsonAsync($"/api/{collection}/{source}/aliases", new { alias = "moved " + source })).EnsureSuccessStatusCode();
        foreach (string action in new[] { "merge/preview", "merge" })
        {
            await AssertRefusedAsync($"/api/{collection}/{source}/{action}", source, 400, "Taxonomy.MergeIntoSelf", "targetKey");
            await AssertRefusedAsync($"/api/{collection}/{source}/{action}", "dat-no-such-target", 400, "Taxonomy.UnknownTarget", "targetKey");
            await AssertRefusedAsync($"/api/{collection}/dat-no-such-source/{action}", target, 404, "ReferenceResource.NotFound");
            // The path's source resolves before the body's target.
            await AssertRefusedAsync($"/api/{collection}/dat-no-such-source/{action}", "dat-no-such-target", 404, "ReferenceResource.NotFound");
            await AssertRefusedAsync($"/api/{collection}/{registeredKey}/{action}", target, 409, "Taxonomy.ReferenceIdentity");
        }

        var preview = await _client.PostAsJsonAsync($"/api/{collection}/{source}/merge/preview", new MergeTaxonomyRequest { TargetKey = target });
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await preview.Content.ReadFromJsonAsync<TaxonomyMergeImpact>())!.Aliases.ShouldBe(2);
        (await _client.GetAsync($"/api/{collection}/{source}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync($"/api/{collection}/{source}/merge", new MergeTaxonomyRequest { TargetKey = target }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _client.GetAsync($"/api/{collection}/{source}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAliasesAsync(collection, target)).ShouldContain(item => item.Alias == "moved " + source);
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("languages")]
    public async Task Aliases_BeyondTheEmbeddedFirstPage_ContinueThroughTheRowsAliasCollection(string collection)
    {
        string key = await AddUnregisteredAsync(collection, "paged", TaxonomyAliasPageRequest.EmbeddedLimit + 4);
        string other = await AddUnregisteredAsync(collection, "other", 1);
        var response = await _client.GetAsync($"/api/{collection}/{key}");
        var embedded = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aliases").Deserialize<Page<AliasResource>>(Json)!;

        embedded.Items.Count.ShouldBe(TaxonomyAliasPageRequest.EmbeddedLimit);
        embedded.HasNextPage.ShouldBeTrue();
        var rest = (await _client.GetFromJsonAsync<Page<AliasResource>>($"/api/{collection}/{key}/aliases?limit=100&cursor={embedded.NextCursor}"))!;
        rest.Items.Count.ShouldBe(5);
        rest.HasNextPage.ShouldBeFalse();
        var all = await ReadAliasesAsync(collection, key, 7);
        all.Select(alias => alias.Id).ShouldBe(embedded.Items.Concat(rest.Items).Select(alias => alias.Id));
        all.Select(alias => IdCoder.TryDecode(alias.Id, out int id) ? id : 0).ShouldBeInOrder();

        var otherRows = (await _client.GetFromJsonAsync<Page<AliasResource>>($"/api/{collection}/{other}/aliases?limit=1"))!.NextCursor;
        var rows = (await _client.GetFromJsonAsync<Page<JsonElement>>($"/api/{collection}?limit=1"))!.NextCursor;
        foreach (string? cursor in new[] { otherRows, rows })
        {
            cursor.ShouldNotBeNull();
            using var misused = await _client.GetAsync($"/api/{collection}/{key}/aliases?cursor={cursor}");
            using var body = await ProblemEnvelope.ReadAsync(misused, 400, "Taxonomy.InvalidCursor");
            body.RootElement.GetProperty("errors").TryGetProperty("cursor", out _).ShouldBeTrue();
        }
        using (var tooLarge = await _client.GetAsync($"/api/{collection}/{key}/aliases?limit=101"))
        using (var body = await ProblemEnvelope.ReadAsync(tooLarge, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").TryGetProperty("limit", out _).ShouldBeTrue();
        using (var unsupported = await _client.GetAsync($"/api/{collection}/{key}/aliases?sortBy=id"))
        using (var body = await ProblemEnvelope.ReadAsync(unsupported, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").TryGetProperty("sortBy", out _).ShouldBeTrue();
        using (var missing = await _client.GetAsync($"/api/{collection}/dat-no-such-row/aliases"))
        using (var _ = await ProblemEnvelope.ReadAsync(missing, 404, "ReferenceResource.NotFound"))
        { }
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("languages")]
    public async Task Aliases_TraversalAcrossAMergeIntoTheRow_NeverRepeatsAndAFreshTraversalSeesEveryAlias(string collection)
    {
        // The source's aliases are older, so the merge moves them in below the running traversal's cursor.
        string source = await AddUnregisteredAsync(collection, "traversed-source", 2);
        string target = await AddUnregisteredAsync(collection, "traversed-target", 5);
        var before = await ReadAliasesAsync(collection, target);
        var moved = await ReadAliasesAsync(collection, source);
        var first = (await _client.GetFromJsonAsync<Page<AliasResource>>($"/api/{collection}/{target}/aliases?limit=2"))!;

        (await _client.PostAsJsonAsync($"/api/{collection}/{source}/merge", new MergeTaxonomyRequest { TargetKey = target }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var traversed = first.Items.ToList();
        for (string? cursor = first.NextCursor; cursor is not null;)
        {
            var page = (await _client.GetFromJsonAsync<Page<AliasResource>>($"/api/{collection}/{target}/aliases?limit=2&cursor={cursor}"))!;
            traversed.AddRange(page.Items);
            cursor = page.NextCursor;
        }

        traversed.Select(alias => alias.Id).ShouldBe(before.Select(alias => alias.Id));
        (await ReadAliasesAsync(collection, target)).Select(alias => alias.Id)
            .ShouldBe(moved.Concat(before).Select(alias => alias.Id), ignoreOrder: true);
        using var gone = await _client.GetAsync($"/api/{collection}/{source}/aliases");
        using var _ = await ProblemEnvelope.ReadAsync(gone, 404, "ReferenceResource.NotFound");
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("languages")]
    public async Task Get_RevalidatesAgainstTheEmbeddedAliasPageOnly(string collection)
    {
        string key = await AddUnregisteredAsync(collection, "revalidated", TaxonomyAliasPageRequest.EmbeddedLimit + 1);
        var response = await _client.GetAsync($"/api/{collection}/{key}");
        var first = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aliases").GetProperty("items")[0]
            .GetProperty("id").GetString()!;

        // An alias past the embedded page leaves the representation, and so its validator, unchanged.
        (await _client.PostAsJsonAsync($"/api/{collection}/{key}/aliases", new { alias = "beyond " + key })).EnsureSuccessStatusCode();
        (await RevalidateAsync($"/api/{collection}/{key}", response.Headers.ETag!)).ShouldBe(HttpStatusCode.NotModified);
        (await _client.DeleteAsync($"/api/{collection}/{key}/aliases/{first}")).EnsureSuccessStatusCode();
        (await RevalidateAsync($"/api/{collection}/{key}", response.Headers.ETag!)).ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("regions")]
    [InlineData("languages")]
    public async Task List_Query_SearchesNamesKeysAndEveryAliasOnTheServer(string collection)
    {
        string key = await AddUnregisteredAsync(collection, "searched", TaxonomyAliasPageRequest.EmbeddedLimit + 2);
        string needle = $"needle{Guid.NewGuid():N}";
        (await _client.PostAsJsonAsync($"/api/{collection}/{key}/aliases", new { alias = "Sunken " + needle })).EnsureSuccessStatusCode();

        var byAlias = (await _client.GetFromJsonAsync<Page<JsonElement>>($"/api/{collection}?query={needle.ToUpperInvariant()}"))!;
        var byKey = (await _client.GetFromJsonAsync<Page<JsonElement>>($"/api/{collection}?query={key}"))!;

        byAlias.Items.Select(item => item.GetProperty("key").GetString()).ShouldBe([key]);
        byKey.Items.Select(item => item.GetProperty("key").GetString()).ShouldBe([key]);
        var unfiltered = (await _client.GetFromJsonAsync<Page<JsonElement>>($"/api/{collection}?limit=1"))!;
        using (var mismatched = await _client.GetAsync($"/api/{collection}?limit=1&query={needle}&cursor={unfiltered.NextCursor}"))
        using (var _ = await ProblemEnvelope.ReadAsync(mismatched, 400, "Taxonomy.InvalidCursor"))
        { }
        using (var unsearchable = await _client.GetAsync($"/api/{collection}?query=!!!"))
        using (var body = await ProblemEnvelope.ReadAsync(unsearchable, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").TryGetProperty("query", out _).ShouldBeTrue();
        // A misspelled filter is refused rather than ignored, which would list every row.
        using (var misspelled = await _client.GetAsync($"/api/{collection}?qeury={needle}"))
        using (var body = await ProblemEnvelope.ReadAsync(misspelled, 400, "Request.InvalidParameter"))
            body.RootElement.GetProperty("errors").TryGetProperty("qeury", out _).ShouldBeTrue();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpStatusCode> RevalidateAsync(string url, System.Net.Http.Headers.EntityTagHeaderValue tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(tag);
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>Every alias of the row, read through its alias collection in id order.</summary>
    private async Task<List<AliasResource>> ReadAliasesAsync(string collection, string key, int limit = 100)
    {
        var aliases = new List<AliasResource>();
        string? cursor = null;
        do
        {
            var page = (await _client.GetFromJsonAsync<Page<AliasResource>>(
                $"/api/{collection}/{key}/aliases?limit={limit}" + (cursor is null ? "" : "&cursor=" + cursor)))!;
            page.Items.Count.ShouldBeLessThanOrEqualTo(limit);
            aliases.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        aliases.Select(alias => alias.Id).Distinct().Count().ShouldBe(aliases.Count);
        return aliases;
    }

    private async Task AssertRefusedAsync(string url, string targetKey, int status, string errorCode, string? field = null)
    {
        using var response = await _client.PostAsJsonAsync(url, new MergeTaxonomyRequest { TargetKey = targetKey });
        using var body = await ProblemEnvelope.ReadAsync(response, status, errorCode);
        if (field is not null)
            body.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    /// <summary>
    ///     An unregistered row as DAT ingest leaves it: a minted key and its creation alias, then
    ///     <paramref name="extraAliases" /> more.
    /// </summary>
    private async Task<string> AddUnregisteredAsync(string collection, string stem = "endpoint", int extraAliases = 0)
    {
        string token = $"{stem} {Guid.NewGuid():N}";
        string key = Romd.PostgreSql.TestSupport.UnregisteredTaxonomyFixtures.KeyFor(token);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        if (collection == "regions")
        {
            var row = new RegionEntity { CanonicalKey = key, Name = token, SortOrder = 999, IsAutoCreated = true };
            db.Regions.Add(row);
            await db.SaveChangesAsync();
            db.RegionAliases.AddRange(Aliases(token).Select(alias => new RegionAliasEntity { RegionId = row.Id, NormalizedAlias = alias }));
        }
        else
        {
            var row = new GameLanguageEntity { CanonicalKey = key, Name = token, Code = "x", SortOrder = 999, IsAutoCreated = true };
            db.GameLanguages.Add(row);
            await db.SaveChangesAsync();
            db.GameLanguageAliases.AddRange(Aliases(token).Select(alias => new GameLanguageAliasEntity { GameLanguageId = row.Id, NormalizedAlias = alias }));
        }
        await db.SaveChangesAsync();
        return key;

        IEnumerable<string> Aliases(string token) => [token, .. Enumerable.Range(1, extraAliases).Select(index => $"{token} {index:D2}")];
    }
}
