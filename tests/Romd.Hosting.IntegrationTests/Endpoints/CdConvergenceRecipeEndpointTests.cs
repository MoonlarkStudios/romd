using Romd.Admin.Application.Source.Rom;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using Romd.Host.Endpoints;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

using ContractOriginalDiscLayout = Romd.Contracts.Management.Models.OriginalDiscLayout;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class CdConvergenceRecipeEndpointTests
{
    private const string Path = "/api/original-disc-layouts/compression-recipe";

    [Theory]
    [InlineData("linux-x64", true)]
    [InlineData("linux-arm64", false)]
    public async Task GetCompressionRecipe_ProductionRegistration_ReturnsTheRegisteredRecipe(string rid, bool available)
    {
        await using var app = Build(RomMediaPolicyComposition.Create(), rid);
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);

        string json = await client.GetStringAsync(Path);

        json.ShouldBe($$"""{"available":{{(available ? "true" : "false")}},"recipeVersion":"chd-ps1-canonical/v1","hunkBytes":39168,"codecs":["cdzs","cdfl"],"qualifiedRid":"linux-x64"}""");
    }

    [Theory]
    [InlineData("linux-arm64", true)]
    [InlineData("linux-x64", false)]
    public async Task GetCompressionRecipe_RegistrationQualifiesAnotherRuntime_ReportsAndAppliesIt(string rid, bool available)
    {
        var registrations = RomMediaPolicyComposition.Registrations.Select(kind => kind.ManifestType == MediaManifestType.CdTracks
            ? kind with { QualifiedRuntime = "linux-arm64" } : kind).ToArray();
        await using var app = Build(RomMediaPolicyComposition.Create(registrations), rid);
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);

        string json = await client.GetStringAsync(Path);

        json.ShouldBe($$"""{"available":{{(available ? "true" : "false")}},"recipeVersion":"chd-ps1-canonical/v1","hunkBytes":39168,"codecs":["cdzs","cdfl"],"qualifiedRid":"linux-arm64"}""");
    }

    [Fact]
    public async Task GetCompressionRecipe_AnotherRegisteredRecipe_FollowsItsIdHunkAndCodecs()
    {
        var registrations = RomMediaPolicyComposition.Registrations.Select(kind => kind.ManifestType == MediaManifestType.CdTracks
            ? kind with
            {
                Recipe = kind.Recipe with { Id = "chd-cd-test/v2", Parameters = "cd;hunk=19584;codecs=6364666c,00000000,00000000,00000000" },
                CdRecipe = (cdKind, platforms) => new RomCdStorageRecipe(cdKind, "chd-cd-test/v2", 2448 * 8, [0x6364666c, 0, 0, 0], platforms)
            }
            : kind).ToArray();
        await using var app = Build(RomMediaPolicyComposition.Create(registrations), "linux-x64");
        await app.StartAsync();
        using var client = app.GetTestClient().WithTestUser(Guid.NewGuid(), roles: [RomdRoleType.Admin]);

        string json = await client.GetStringAsync(Path);

        json.ShouldBe("""{"available":true,"recipeVersion":"chd-cd-test/v2","hunkBytes":19584,"codecs":["cdfl"],"qualifiedRid":"linux-x64"}""");
    }

    /// <summary>An admin host whose verified resident decoder runs on <paramref name="rid" />; it never encodes.</summary>
    private static WebApplication Build(RomMediaComposition media, string rid)
    {
        var native = new ChdNativeIdentity(rid, "libmoonlark_chdr.so", 100, new string('c', 64), new string('a', 40), "test-only-upstream", 2,
            "test-only-family", new string('e', 64), true, true, true);
        var capabilities = new MediaHostCapabilities(() => native, () => null, media, encodes: false);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTestAuthentication();
        builder.Services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, CurrentUser>();
        builder.Services.AddSingleton(media.CdRecipe!);
        builder.Services.AddSingleton<IMediaHostCapabilities>(capabilities);
        builder.Services.AddSingleton<IReferenceCatalogService>(TestReferenceCatalog.Create());
        builder.Services.AddSingleton(Substitute.For<IOriginalDiscLayoutService>());
        builder.Services.AddSingleton(Substitute.For<ICommandHandler<DecideOriginalDiscLayoutCommand, ContractOriginalDiscLayout>>());
        builder.Services.AddSingleton(Substitute.For<IRomCdArchiveDownloadCatalog>());
        builder.Services.AddSingleton(Substitute.For<IRomCdArchiveDownloadReader>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/api").MapAdminOriginalDiscLayoutEndpoints();
        return app;
    }
}
