using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.Libraries;
using Romd.Contracts.Management.Auth;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Shouldly;
using Xunit;

using JobReference = Romd.Contracts.Management.Models.JobReference;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class StarterLibraryEndpointsTests
{
    [Fact]
    public async Task ReviewAndPublish_StarterLibrary_BootstrapAdminCanBrowseOwnedGames()
    {
        const string email = "starter-test@localhost";
        const string password = "StarterFixture23!";
        const string portalOrigin = "http://localhost:5174";
        await using var fixture = await ConsumerDeliveryEndpointsTests.ConsumerDeliveryFixture.CreateAsync();
        await fixture.SeedOpenIddictAsync();
        await using var adminFactory = fixture.CreateAdminHost();
        await using var adminHost = adminFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Romd:DefaultAdminEmail", email);
            builder.UseSetting("Romd:DefaultAdminPassword", password);
            builder.UseSetting("Romd:Auth:ConsumerSpa:RedirectUris:0", portalOrigin + "/auth/callback");
            builder.ConfigureTestServices(services =>
            {
                services.AddRomdWorkerCurationServices().AddRomdJobExecution();
                services.AddRomdNotifications();
                services.AddScoped<JobRunner<MaterializationJob>>();
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IMaterializationJobEnqueuer>();
                services.AddSingleton(Substitute.For<IMaterializationJobEnqueuer>());
            });
        });
        var seeder = ActivatorUtilities.CreateInstance<AdminSeeder>(adminHost.Services);
        await seeder.StartAsync(default);

        var seed = await fixture.SeedDeliveryDataAsync();
        Guid adminId;
        await using (var db = fixture.CreateSetupDbContext())
        {
            adminId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            (await db.TrackedTitles.AnyAsync(tracked => tracked.TitleId == seed.OwnedTitleId)).ShouldBeFalse();
            (await db.TitleContentRatings.AnyAsync(rating => rating.TitleId == seed.OwnedTitleId)).ShouldBeFalse();
        }

        using var admin = adminHost.CreateClient().WithTestUser(adminId, email, [RomdRoleType.Admin]);
        var json = adminHost.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var profile = (await admin.GetFromJsonAsync<CurrentUserResponse>("/api/me")).ShouldNotBeNull();
        profile.ConsumerPortalUrl.ShouldBe(portalOrigin);
        var libraryPath = $"/api/libraries/{profile.LibraryId.ShouldNotBeNull()}";
        var draft = (await admin.GetFromJsonAsync<Library>(libraryPath, json)).ShouldNotBeNull();
        draft.Name.ShouldBe("All Titles");
        draft.StarterSetupComplete.ShouldBe(false);
        draft.IsDefault.ShouldBeFalse();
        draft.LastMaterializedAt.ShouldBeNull();

        using var consumer = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var token = await OidcAuthorizationCodeFlow.RunAsync(consumer, RomdOpenIddictClients.ConsumerSpa,
            portalOrigin + "/auth/callback", email, password);
        consumer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["access_token"].GetString());
        (await consumer.GetAsync("/api/me/library")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The same explicit setting used for any library can select the starter.
        using var selected = await admin.PatchAsJsonAsync(libraryPath, new UpdateLibraryRequest(draft.Name, IsDefault: true));
        selected.EnsureSuccessStatusCode();
        (await selected.Content.ReadFromJsonAsync<Library>(json)).ShouldNotBeNull().IsDefault.ShouldBeTrue();

        var review = (await admin.GetFromJsonAsync<LibraryPublicationReview>($"{libraryPath}/publication/review")).ShouldNotBeNull();
        review.AssignedAccounts.ShouldBe(1);
        review.PublishedGames.ShouldBe(0);
        review.AddedExamples.ShouldContain(game => game.Id == IdCoder.Encode(seed.OwnedTitleId));
        review.AddedExamples.ShouldNotContain(game => game.Id == IdCoder.Encode(seed.ExposedUnownedTitleId));

        using var accepted = await admin.PostAsync($"{libraryPath}/materialize?reviewedRevision={review.Revision}", null);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var jobId = (await accepted.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull().JobId;
        await using (var worker = adminHost.Services.CreateAsyncScope())
        {
            // Execute the durable accepted job without starting a background dispatch loop in this fixture.
            await worker.ServiceProvider.GetRequiredService<JobRunner<MaterializationJob>>().RunAsync(jobId, "starter-test", default);
        }

        var publication = (await admin.GetFromJsonAsync<LibraryPublicationStatus>($"{libraryPath}/publication")).ShouldNotBeNull();
        publication.LatestAttempt.ShouldNotBeNull().Outcome.ShouldBe("Published");
        var published = (await admin.GetFromJsonAsync<Library>(libraryPath, json)).ShouldNotBeNull();
        published.StarterSetupComplete.ShouldBe(true);
        var context = (await consumer.GetFromJsonAsync<LibraryContext>("/api/me/library")).ShouldNotBeNull();
        context.Name.ShouldBe("All Titles");
        var titles = (await consumer.GetFromJsonAsync<Page<ConsumerTitleCard>>("/api/catalog")).ShouldNotBeNull();
        titles.Items.ShouldContain(title => title.Id == IdCoder.Encode(seed.OwnedTitleId));
        titles.Items.ShouldNotContain(title => title.Id == IdCoder.Encode(seed.ExposedUnownedTitleId));
    }
}
