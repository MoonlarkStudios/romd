using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Romd.Contracts.Consumer.Account;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [Fact]
    public Task Vfs_ContentAdmission_QueuesAtAccountLimitWhileOtherAccountProgresses() =>
        RunVfsHttpAdmissionScenarioAsync(overload: false);

    [Fact]
    public Task Vfs_ContentAdmission_FullQueueTimeoutAndCancellationRecoverAllCapacity() =>
        RunVfsHttpAdmissionScenarioAsync(overload: true);

    private static async Task RunVfsHttpAdmissionScenarioAsync(bool overload)
    {
        var options = new ConsumerVfsOptions { Enabled = true };
        if (overload)
        {
            options.MaximumQueuedRequests = 1;
            options.MaximumQueueWaitMilliseconds = 5000;
        }
        await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
        {
            services.AddSingleton(options);
            services.Configure<ContentStoreOptions>(store => store.WritePolicy = ContentWritePolicy.Raw);
        }, configureHost: builder => builder.UseSetting("Romd:ConsumerHost:PublicUrl", "https://library.example.test"));
        await using var source = await VfsProbeContent.CreateAsync();
        int library = await fixture.CreateLibraryAsync("Admission");
        string relative = await fixture.SeedVfsProbeAsync(library, source);
        string route = "/vfs/library/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        byte[] expected = source.ReadRange(65530, 4096);
        var ownerUser = await fixture.CreateConsumerUserAsync("admission-owner", "admission-owner@localhost", "Password123",
            user => user.LibraryId = library);
        var otherUser = await fixture.CreateConsumerUserAsync("admission-other", "admission-other@localhost", "Password123",
            user => user.LibraryId = library);
        Guid ownerId = ownerUser.Id;
        using var owner = fixture.CreateAuthenticatedClient(ownerId, ownerUser.Email!, library);
        using var other = fixture.CreateAuthenticatedClient(otherUser.Id, otherUser.Email!, library);
        async Task<VfsConnectionCredential> IssueAsync(HttpClient account)
        {
            using var response = await account.PostAsJsonAsync("/api/me/connections", new SaveVfsConnectionRequest("Admission"));
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            return (await response.Content.ReadFromJsonAsync<VfsConnectionCredential>())!;
        }
        var ownerCredential = await IssueAsync(owner);
        var otherCredential = await IssueAsync(other);
        using var anonymous = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var admission = fixture.Factory.Services.GetRequiredService<ConsumerVfsAdmission>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pendingStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var held = new List<IDisposable>();
        var pending = new List<Task<HttpResponseMessage>>();

        async Task<HttpResponseMessage> ReadAsync(VfsConnectionCredential credential, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(credential.Connection.Username + ":" + credential.Password)));
            request.Headers.Range = new RangeHeaderValue(65530, 65530 + expected.Length - 1);
            return await anonymous.SendAsync(request, token);
        }
        async Task VerifyRangeAsync(Task<HttpResponseMessage> read)
        {
            using var response = await read;
            response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            (await response.Content.ReadAsByteArrayAsync(deadline.Token)).ShouldBe(expected);
        }
        async Task VerifyRejectedAsync(Task<HttpResponseMessage> read)
        {
            using var response = await read;
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter?.Delta.ShouldBe(TimeSpan.FromSeconds(1));
            (await response.Content.ReadAsByteArrayAsync(deadline.Token)).ShouldBeEmpty();
        }
        async Task WaitForQueueAsync(int expectedCount)
        {
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            poll.CancelAfter(TimeSpan.FromSeconds(5));
            while (admission.QueuedRequestCount != expectedCount)
                await Task.Delay(10, poll.Token);
        }

        await VfsProbeEvidence.RunAsync(async _ =>
        {
            for (int index = 0; index < 4; index++)
                held.Add((await admission.EnterAsync(ownerId, ownerCredential.Connection.Id, deadline.Token)).ShouldNotBeNull());
            var queued = ReadAsync(ownerCredential, pendingStop.Token);
            pending.Add(queued);
            await WaitForQueueAsync(1);
            queued.IsCompleted.ShouldBeFalse();
            // Assert repeated HTTP progress while the first account remains at its cap.
            for (int index = 0; index < 3; index++)
                await VerifyRangeAsync(ReadAsync(otherCredential, deadline.Token));
            queued.IsCompleted.ShouldBeFalse();
            admission.QueuedRequestCount.ShouldBe(1);

            if (overload)
            {
                await VerifyRejectedAsync(ReadAsync(ownerCredential, deadline.Token));
                queued.IsCompleted.ShouldBeFalse("queue exhaustion must reject before the original waiter times out");
                admission.QueuedRequestCount.ShouldBe(1);
                await pendingStop.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(async () => { using var response = await queued; });
                await WaitForQueueAsync(0);
                // Reusing the queue after cancellation verifies its capacity was returned.
                var timeout = ReadAsync(ownerCredential, deadline.Token);
                pending.Add(timeout);
                await WaitForQueueAsync(1);
                timeout.IsCompleted.ShouldBeFalse();
                await VerifyRejectedAsync(timeout);
                await WaitForQueueAsync(0);
            }
            else
            {
                var second = ReadAsync(ownerCredential, pendingStop.Token);
                pending.Add(second);
                await WaitForQueueAsync(2);
                second.IsCompleted.ShouldBeFalse();
                held[0].Dispose();
                held.RemoveAt(0);
                await VerifyRangeAsync(queued);
                await VerifyRangeAsync(second);
                await WaitForQueueAsync(0);
            }
            return true;
        }, _ => Task.CompletedTask, async _ =>
        {
            await VfsAdmissionCleanup.RunAsync(pendingStop, held, pending);
            held.Clear();
        }, diagnostic => Console.Error.WriteLine(diagnostic), deadline.Token);
        await WaitForQueueAsync(0);
        await VerifyRangeAsync(ReadAsync(ownerCredential, deadline.Token));
        // Recovering all four slots detects leaks that one successful read would miss.
        try
        {
            for (int index = 0; index < 4; index++)
                held.Add((await admission.EnterAsync(ownerId, ownerCredential.Connection.Id, deadline.Token)).ShouldNotBeNull());
            admission.QueuedRequestCount.ShouldBe(0);
        }
        finally
        {
            foreach (var lease in held)
                lease.Dispose();
        }
    }
}
