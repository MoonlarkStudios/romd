using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Common.Models;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class EnrichmentWorkbenchEndpointsTests(IntegrationTestFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task Workbench_RequiresManagerForReadingAndDecisions(bool authenticated, HttpStatusCode expected)
    {
        using var client = fixture.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.User]);
        (await client.GetAsync("/api/enrichment/workbench")).StatusCode.ShouldBe(expected);
        (await client.GetAsync("/api/enrichment/workbench/stats")).StatusCode.ShouldBe(expected);
        (await client.GetAsync($"/api/titles/{IdCoder.Encode(1)}/enrichment-assessment")).StatusCode.ShouldBe(expected);
        (await client.SendAsync(Omission(IdCoder.Encode(1), true, $"\"{Guid.NewGuid()}\""))).StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Manager_CanReviewWithoutJobAndPersistPreconditionedOmission()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var name = $"Workbench {Guid.NewGuid():N}";
        var title = new TitleEntity
        {
            PlatformId = await db.Platforms.Select(x => x.Id).FirstAsync(),
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        db.TrackedTitles.Add(new() { TitleId = title.Id });
        await db.SaveChangesAsync();
        var id = IdCoder.Encode(title.Id);
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);

        var page = await client.GetFromJsonAsync<Page<EnrichmentAssessment>>($"/api/enrichment/workbench?view=All&query={Uri.EscapeDataString(name)}", JsonOptions);
        page.ShouldNotBeNull();
        page.Items.ShouldHaveSingleItem().TitleId.ShouldBe(id);
        var counts = await client.GetFromJsonAsync<EnrichmentWorkbenchCounts>($"/api/enrichment/workbench/stats?query={Uri.EscapeDataString(name)}");
        counts.ShouldNotBeNull().Total.ShouldBe(1);
        var emptyCounts = await client.GetFromJsonAsync<EnrichmentWorkbenchCounts>($"/api/enrichment/workbench/stats?query={Guid.NewGuid():N}");
        emptyCounts.ShouldBe(new EnrichmentWorkbenchCounts(0, 0, 0, 0, 0));
        var current = await client.GetFromJsonAsync<EnrichmentAssessment>($"/api/titles/{id}/enrichment-assessment", JsonOptions);
        current.ShouldNotBeNull();
        current.ThumbnailUrl.ShouldBeNull();
        current.PlatformCompactLabel.ShouldNotBeNullOrWhiteSpace();
        page.Items[0].PlatformCompactLabel.ShouldBe(current.PlatformCompactLabel);
        current.Requirements.ShouldContain(requirement => requirement.State != EnrichmentIssueState.Satisfied);
        current.Requirements.Single(requirement => requirement.Key == "Metadata:Refresh").SatisfiedBy
            .ShouldBe(EnrichmentRequirementSatisfiedBy.Current);
        using var wire = JsonDocument.Parse(await client.GetStringAsync($"/api/titles/{id}/enrichment-assessment"));
        wire.RootElement.TryGetProperty("issues", out _).ShouldBeFalse();
        var requirements = wire.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
        requirements.Select(requirement => requirement.GetProperty("key").GetString()).ShouldBe(current.Requirements.Select(x => x.Key));
        foreach (var requirement in requirements)
        {
            requirement.TryGetProperty("reason", out _).ShouldBeFalse();
            var satisfied = requirement.GetProperty("state").GetString() == nameof(EnrichmentIssueState.Satisfied);
            // Both keys are always present; exactly one carries a string value.
            requirement.GetProperty("cause").ValueKind.ShouldBe(satisfied ? JsonValueKind.Null : JsonValueKind.String);
            requirement.GetProperty("satisfiedBy").ValueKind.ShouldBe(satisfied ? JsonValueKind.String : JsonValueKind.Null);
        }
        var description = requirements.Single(requirement => requirement.GetProperty("key").GetString() == "Metadata:Description");
        description.GetProperty("cause").GetString()
            .ShouldBe(current.Requirements.Single(x => x.Key == "Metadata:Description").Cause.ToString());
        requirements.Single(requirement => requirement.GetProperty("key").GetString() == "Metadata:Refresh")
            .GetProperty("satisfiedBy").GetString().ShouldBe(nameof(EnrichmentRequirementSatisfiedBy.Current));
        var readTag = $"\"{current.Revision}\"";
        using (var unconditional = await client.SendAsync(Omission(id, true, null)))
        using (await ProblemEnvelope.ReadAsync(unconditional, 428, "Titles.PreconditionRequired"))
        { }
        using var saved = await client.SendAsync(Omission(id, true, readTag));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        using (var detail = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
            $"\"{detail.RootElement.GetProperty("revision").GetString()}\"".ShouldBe(saved.Headers.ETag!.Tag);
        saved.Headers.ETag!.Tag.ShouldNotBe(readTag);
        var assessment = await client.GetFromJsonAsync<EnrichmentAssessment>($"/api/titles/{id}/enrichment-assessment", JsonOptions);
        assessment.ShouldNotBeNull();
        assessment.AcceptedOmissions.ShouldContain("Genre");
        assessment.Requirements.Single(requirement => requirement.Key == "Metadata:Genre").SatisfiedBy
            .ShouldBe(EnrichmentRequirementSatisfiedBy.AcceptedOmission);
        $"\"{assessment.Revision}\"".ShouldBe(saved.Headers.ETag.Tag);
        using (var stale = await client.SendAsync(Omission(id, false, readTag)))
        using (await ProblemEnvelope.ReadAsync(stale, 412, "Titles.PreconditionFailed"))
        { }
        using var reopened = await client.SendAsync(Omission(id, false, saved.Headers.ETag.Tag));
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<EnrichmentAssessment>($"/api/titles/{id}/enrichment-assessment", JsonOptions))!
            .AcceptedOmissions.ShouldNotContain("Genre");
    }

    private static HttpRequestMessage Omission(string titleId, bool accepted, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/titles/{titleId}/enrichment-omissions/Genre")
        {
            Content = JsonContent.Create(new SetEnrichmentOmissionRequest(accepted))
        };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch).ShouldBeTrue();
        return request;
    }

    [Fact]
    public async Task WorkbenchCounts_InvalidSearch_ReturnsValidationProblem()
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);
        var response = await client.GetAsync($"/api/enrichment/workbench/stats?query={new string('x', 201)}");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("view=Unknown")]
    [InlineData("cursor=!")]
    [InlineData("limit=101")]
    public async Task Workbench_InvalidQuery_ReturnsValidationProblem(string query)
    {
        using var client = fixture.CreateClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Manager]);
        var response = await client.GetAsync($"/api/enrichment/workbench?{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }
}
