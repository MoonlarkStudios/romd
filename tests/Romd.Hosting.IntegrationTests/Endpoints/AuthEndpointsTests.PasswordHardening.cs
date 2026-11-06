using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Management.Auth;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class AuthEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangePassword_OverlongInput_RejectsAndPreservesCredentials(bool current)
    {
        string email = $"password-length-{Guid.NewGuid():N}@localhost";
        Guid id = await CreateAdminUserAsync(email, "Password123");
        using var host = fixture.WithWebHostBuilder(_ => { });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .WithTestUser(id, email, [RomdRoleType.Admin]);
        string overlong = "Aa1" + new string('x', 4094);

        using var response = await client.PutAsJsonAsync("/api/me/password",
            new ChangePasswordRequest(current ? overlong : "Password123", current ? "NewPassword123" : overlong));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("PasswordTooLong");
        (await SubmitPasswordLoginAsync(client, email, "Password123")).ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task ChangePassword_MaximumLengthUnicodePassword_CanSignInThroughEncodedForm()
    {
        string email = $"password-unicode-{Guid.NewGuid():N}@localhost";
        Guid id = await CreateAdminUserAsync(email, "Password123");
        using var host = fixture.WithWebHostBuilder(_ => { });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .WithTestUser(id, email, [RomdRoleType.Admin]);
        string password = "Aa1" + new string('\u4e00', 4093);

        using var response = await client.PutAsJsonAsync("/api/me/password", new ChangePasswordRequest("Password123", password));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SubmitPasswordLoginAsync(client, email, password)).ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task CreateUser_OverlongPassword_DoesNotCreateAccount()
    {
        using var client = fixture.CreateAuthenticatedClient();
        string email = $"create-length-{Guid.NewGuid():N}@localhost";

        using var response = await client.PostAsJsonAsync("/api/users",
            new CreateUserRequest(email, "Aa1" + new string('x', 4094), "User"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = fixture.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>().FindByEmailAsync(email)).ShouldBeNull();
    }

    [Fact]
    public async Task UpdateUser_OverlongPassword_DoesNotResetCredentials()
    {
        string email = $"reset-length-{Guid.NewGuid():N}@localhost";
        Guid id = await CreateAdminUserAsync(email, "Password123");
        using var host = fixture.WithWebHostBuilder(_ => { });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .WithTestUser(id, email, [RomdRoleType.Admin]);

        using var response = await client.PutAsJsonAsync($"/api/users/{id}",
            new UpdateUserRequest(null, "Aa1" + new string('x', 4094)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SubmitPasswordLoginAsync(client, email, "Password123")).ShouldBe(HttpStatusCode.Redirect);
    }

    [Theory]
    [InlineData("activation")]
    [InlineData("recovery")]
    public async Task RedeemAccountLink_OverlongPassword_PreservesLinkForValidRedemption(string purpose)
    {
        using var client = fixture.CreateAuthenticatedClient();
        string email = $"link-length-{Guid.NewGuid():N}@localhost";
        using var created = await client.PostAsJsonAsync("/api/users",
            new CreateUserRequest(email, "Password123", "User", RequiresActivation: purpose == "activation"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        using var issued = await client.PostAsJsonAsync($"/api/users/{user.Id}/links", new IssueAccountLinkRequest(purpose));
        issued.StatusCode.ShouldBe(HttpStatusCode.Created);
        var link = (await issued.Content.ReadFromJsonAsync<IssuedAccountLinkDto>())!;

        using var rejected = await client.PostAsJsonAsync("/api/auth/account-link",
            new RedeemAccountLinkRequest(link.Token, "Aa1" + new string('x', 4094)));
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var accepted = await client.PostAsJsonAsync("/api/auth/account-link",
            new RedeemAccountLinkRequest(link.Token, "RecoveredPassword123"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        (await users.CheckPasswordAsync((await users.FindByIdAsync(user.Id.ToString()))!, "RecoveredPassword123")).ShouldBeTrue();
    }

    private static async Task<HttpStatusCode> SubmitPasswordLoginAsync(HttpClient client, string login, string password)
    {
        using var form = await BrowserAuthenticationForm.CreateAsync(client, "/connect/login",
            new Dictionary<string, string> { ["login"] = login, ["password"] = password });
        using var response = await client.PostAsync("/connect/login", form);
        return response.StatusCode;
    }
}
