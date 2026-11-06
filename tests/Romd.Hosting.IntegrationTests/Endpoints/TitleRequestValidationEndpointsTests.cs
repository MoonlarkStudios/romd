using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Management.Titles;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Strict validation of the title family's request bodies (docs/decisions/api-contract-policy.md,
///     "Strict Validation"): a malformed body id or an unknown body system key is a 400 keyed by its field, and a
///     rejected body writes nothing.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TitleRequestValidationEndpointsTests(IntegrationTestFixture fixture)
{
    [Theory]
    [InlineData("merge", "sourceTitleId", "Titles.InvalidTitleId", "not-an-id")]
    [InlineData("merge", "sourceTitleId", "Titles.InvalidTitleId", "")]
    [InlineData("move", "targetTitleId", "Titles.InvalidTargetTitleId", "not-an-id")]
    [InlineData("move", "targetTitleId", "Titles.InvalidTargetTitleId", "")]
    [InlineData("move", "targetTitleId", "Titles.InvalidTargetTitleId", "  ")]
    [InlineData("tracking", "pinnedCatalogReleaseId", "Titles.InvalidPinnedCatalogReleaseId", "not-an-id")]
    [InlineData("tracking", "pinnedCatalogReleaseId", "Titles.InvalidPinnedCatalogReleaseId", "")]
    [InlineData("tracking", "pinnedCatalogReleaseId", "Titles.InvalidPinnedCatalogReleaseId", " ")]
    [InlineData("batch-tracking", "titleIds", "Titles.InvalidTrackingSelector", "not-an-id")]
    [InlineData("batch-tracking", "titleIds", "Titles.InvalidTrackingSelector", "")]
    [InlineData("batch-tracking", "datSourceId", "Titles.InvalidTrackingSelector", "not-an-id")]
    [InlineData("batch-tracking", "datSourceId", "Titles.InvalidTrackingSelector", "")]
    [InlineData("batch-tracking", "datSourceId", "Titles.InvalidTrackingSelector", "\t")]
    public async Task MalformedBodyId_Is400KeyedByItsField_AndWritesNothing(string operation, string field,
        string errorCode, string malformed)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var revision = await StoredRevisionAsync(titleId);

        using var response = operation switch
        {
            "merge" => await client.PostAsJsonAsync($"/api/titles/{titleId}/merge",
                new { sourceTitleId = malformed, sourceTitleRevision = Guid.NewGuid().ToString() }),
            "move" => await client.PostAsJsonAsync($"/api/titles/{titleId}/games/{IdCoder.Encode(1)}/move",
                new { targetTitleId = malformed }),
            "tracking" => await client.PutAsJsonAsync($"/api/titles/{titleId}/tracking",
                new { tracked = true, pinnedCatalogReleaseId = malformed }),
            // The valid id beside the malformed one shows the request is rejected whole, not applied in part.
            _ => await client.PostAsJsonAsync("/api/titles/batch-set-tracking", field == "titleIds"
                ? new { titleIds = new[] { titleId, malformed }, tracked = true }
                : (object)new { datSourceId = malformed, tracked = true })
        };

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, errorCode);
        problem.RootElement.GetProperty("errors").GetProperty(field).GetArrayLength().ShouldBe(1);
        (await StoredRevisionAsync(titleId)).ShouldBe(revision);
        (await IsTrackedAsync(titleId)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("no-such-system")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BatchTracking_UnknownOrBlankSystemKey_Is400KeyedBySystemKey(string systemKey)
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/api/titles/batch-set-tracking",
            new { systemKey, tracked = true });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Systems.UnknownKey");
        problem.RootElement.GetProperty("errors").GetProperty("systemKey").GetArrayLength().ShouldBe(1);
        problem.RootElement.GetRawText().ShouldNotContain("no-such-system");
    }

    [Theory]
    [InlineData("PATCH", "enrichment")]
    public async Task FamilyWrite_MissingRequiredMember_Is400AndWritesNothing(string method, string suffix)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var revision = await StoredRevisionAsync(titleId);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/titles/{titleId}/{suffix}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"").ShouldBeTrue();

        using var response = await client.SendAsync(request);

        using (await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody"))
        { }
        (await StoredRevisionAsync(titleId)).ShouldBe(revision);
    }

    [Fact]
    public async Task Tracking_PinWhileUntracking_Is400()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();

        using var response = await client.PutAsJsonAsync($"/api/titles/{titleId}/tracking",
            new { tracked = false, pinnedCatalogReleaseId = IdCoder.Encode(1) });

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, "Titles.InvalidPinnedCatalogReleaseId");
        problem.RootElement.GetProperty("errors").TryGetProperty("pinnedCatalogReleaseId", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"bogus":"x"}""")]
    [InlineData("""{"genre":"Puzzle","genre":"Action"}""")]
    [InlineData("""{"players":"two"}""")]
    public async Task Patch_UnknownDuplicateOrMistypedField_Is400AndWritesNothing(string body)
    {
        using var client = fixture.CreateAuthenticatedClient();
        var titleId = await SeedTitleAsync();
        var revision = await StoredRevisionAsync(titleId);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/titles/{titleId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"").ShouldBeTrue();

        using var response = await client.SendAsync(request);

        using (await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidBody"))
        { }
        (await StoredRevisionAsync(titleId)).ShouldBe(revision);
    }

    [Fact]
    public void PatchFields_EachNameAFieldTheCommandSupplies()
    {
        // The endpoint maps each supplied field name to its flag; a field without one would fail at run time.
        var fields = ReferencePatchJsonConverter.EditableProperties(typeof(UpdateTitleMetadataRequest))
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

        fields.ShouldBe(Enum.GetNames<TitleMetadataFields>().Where(name => name != nameof(TitleMetadataFields.None))
            .Order(StringComparer.Ordinal).ToArray());
        new UpdateTitleMetadataRequest().ClearableFields
            .ShouldBe(fields.Select(JsonNamingPolicy.CamelCase.ConvertName), ignoreOrder: true);
    }

    private async Task<Guid> StoredRevisionAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles
            .Where(title => title.Id == id).Select(title => title.Revision).SingleAsync();
    }

    private async Task<bool> IsTrackedAsync(string titleId)
    {
        using var scope = fixture.Services.CreateScope();
        var id = Sqid.Parse(titleId, null).Value;
        return await scope.ServiceProvider.GetRequiredService<RomdDbContext>().TrackedTitles
            .AnyAsync(tracked => tracked.TitleId == id);
    }

    private async Task<string> SeedTitleAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Validation Test Title {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = await db.Platforms.OrderBy(platform => platform.Id).Select(platform => platform.Id).FirstAsync(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return IdCoder.Encode(title.Id);
    }
}
