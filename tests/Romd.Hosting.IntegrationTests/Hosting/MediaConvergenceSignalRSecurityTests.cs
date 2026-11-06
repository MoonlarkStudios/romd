using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Host.Authorization;
using Romd.Host.Hubs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class MediaConvergenceSignalRSecurityTests
{
    private const string ConnectionId = "convergence-test";

    [Theory]
    [InlineData(false, RomdRoleType.User)]
    [InlineData(false, RomdRoleType.Contributor)]
    [InlineData(false, RomdRoleType.Manager)]
    [InlineData(true, RomdRoleType.User)]
    [InlineData(true, RomdRoleType.Contributor)]
    [InlineData(true, RomdRoleType.Manager)]
    [InlineData(null, RomdRoleType.User)]
    [InlineData(null, RomdRoleType.Contributor)]
    [InlineData(null, RomdRoleType.Manager)]
    public async Task SubscribeToJob_OwnedMediaWithoutAdmin_DeniesJoin(bool? bulk, RomdRoleType role)
    {
        Guid actorId = Guid.NewGuid();
        Job job = MediaJob(bulk, actorId);
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var groups = Substitute.For<IGroupManager>();
        using var services = Services(repository, actorId, role);
        using var hub = CreateHub(services, groups, actorId, role);

        await Should.ThrowAsync<HubException>(() => hub.SubscribeToJob(job.Id.ToString()));

        await groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task SubscribeToJob_MediaWithAdmin_UsesAuthorizedGroup(bool? bulk)
    {
        Job job = MediaJob(bulk, Guid.NewGuid());
        Guid actorId = Guid.NewGuid();
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var groups = Substitute.For<IGroupManager>();
        using var services = Services(repository, actorId, RomdRoleType.Admin);
        using var hub = CreateHub(services, groups, actorId, RomdRoleType.Admin);

        await hub.SubscribeToJob(job.Id.ToString().ToUpperInvariant());

        await groups.Received(1).AddToGroupAsync(ConnectionId, $"job:admin:{job.Id}", Arg.Any<CancellationToken>());
        await groups.DidNotReceive().AddToGroupAsync(ConnectionId, $"job:{job.Id}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscribeToJob_OrdinaryManagerJoin_PreservesExistingGroup()
    {
        var job = UploadJob.Create("ordinary.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid());
        Guid actorId = Guid.NewGuid();
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var groups = Substitute.For<IGroupManager>();
        using var services = Services(repository, actorId, RomdRoleType.Manager);
        using var hub = CreateHub(services, groups, actorId, RomdRoleType.Manager);

        await hub.SubscribeToJob(job.Id.ToString());

        await groups.Received(1).AddToGroupAsync(ConnectionId, $"job:{job.Id}", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RomdRoleType.User)]
    [InlineData(RomdRoleType.Contributor)]
    [InlineData(RomdRoleType.Manager)]
    public async Task SubscribeToJobFeed_NonAdmin_DoesNotJoinMediaFeed(RomdRoleType role)
    {
        Guid actorId = Guid.NewGuid();
        var groups = Substitute.For<IGroupManager>();
        using var services = Services(Substitute.For<IJobRepository>(), actorId, role);
        using var hub = CreateHub(services, groups, actorId, role);

        await hub.SubscribeToJobFeed();

        await groups.Received(1).AddToGroupAsync(ConnectionId, "jobs:all", Arg.Any<CancellationToken>());
        await groups.DidNotReceive().AddToGroupAsync(ConnectionId, "jobs:admin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscribeToJobFeed_Admin_JoinsBothFeeds()
    {
        Guid actorId = Guid.NewGuid();
        var groups = Substitute.For<IGroupManager>();
        using var services = Services(Substitute.For<IJobRepository>(), actorId, RomdRoleType.Admin);
        using var hub = CreateHub(services, groups, actorId, RomdRoleType.Admin);

        await hub.SubscribeToJobFeed();

        await groups.Received(1).AddToGroupAsync(ConnectionId, "jobs:all", Arg.Any<CancellationToken>());
        await groups.Received(1).AddToGroupAsync(ConnectionId, "jobs:admin", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-a-job")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task SubscribeToJob_InvalidIdentity_DoesNotJoinAnyGroup(string jobId)
    {
        Guid actorId = Guid.NewGuid();
        var groups = Substitute.For<IGroupManager>();
        using var services = Services(Substitute.For<IJobRepository>(), actorId, RomdRoleType.Admin);
        using var hub = CreateHub(services, groups, actorId, RomdRoleType.Admin);

        await Should.ThrowAsync<HubException>(() => hub.SubscribeToJob(jobId));

        await groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task SendJobUpdated_MediaPayload_DeliversOnlyToAuthorizedGroups(bool? bulk)
    {
        const int schemaVersion = 9;
        Job job = MediaJob(bulk, Guid.NewGuid());
        var keys = await TestReferenceCatalog.Create().GetSystemKeysAsync(CancellationToken.None);
        var dto = job.ToContract(keys);
        var context = Substitute.For<IHubContext<JobHub>>();
        var clients = Substitute.For<IHubClients>();
        var perJob = Substitute.For<IClientProxy>();
        var feed = Substitute.For<IClientProxy>();
        var ordinary = Substitute.For<IClientProxy>();
        context.Clients.Returns(clients);
        clients.Group($"job:admin:{job.Id}").Returns(perJob);
        clients.Group("jobs:admin").Returns(feed);
        clients.Group($"job:{job.Id}").Returns(ordinary);
        clients.Group("jobs:all").Returns(ordinary);
        using var cancellation = new CancellationTokenSource();

        await new SignalRJobNotifier(context).SendJobUpdatedAsync(dto, schemaVersion, cancellation.Token);

        await perJob.Received(1).SendCoreAsync("JobUpdated", Arg.Is<object?[]>(arguments =>
            arguments.Length == 2 && ReferenceEquals(arguments[0], dto) && Equals(arguments[1], schemaVersion)), cancellation.Token);
        await feed.Received(1).SendCoreAsync("JobUpdated", Arg.Is<object?[]>(arguments =>
            arguments.Length == 2 && ReferenceEquals(arguments[0], dto) && Equals(arguments[1], schemaVersion)), cancellation.Token);
        await ordinary.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);
    }

    [Fact]
    public async Task SendJobUpdated_OrdinaryPayload_PreservesExistingGroups()
    {
        var job = UploadJob.Create("ordinary.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: Guid.NewGuid());
        var keys = await TestReferenceCatalog.Create().GetSystemKeysAsync(CancellationToken.None);
        var dto = job.ToContract(keys);
        var context = Substitute.For<IHubContext<JobHub>>();
        var clients = Substitute.For<IHubClients>();
        var perJob = Substitute.For<IClientProxy>();
        var feed = Substitute.For<IClientProxy>();
        var admin = Substitute.For<IClientProxy>();
        context.Clients.Returns(clients);
        clients.Group($"job:{job.Id}").Returns(perJob);
        clients.Group("jobs:all").Returns(feed);
        clients.Group($"job:admin:{job.Id}").Returns(admin);
        clients.Group("jobs:admin").Returns(admin);

        await new SignalRJobNotifier(context).SendJobUpdatedAsync(dto, 1, CancellationToken.None);

        await perJob.Received(1).SendCoreAsync("JobUpdated", Arg.Any<object?[]>(), CancellationToken.None);
        await feed.Received(1).SendCoreAsync("JobUpdated", Arg.Any<object?[]>(), CancellationToken.None);
        await admin.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(RomdRoleType.Manager, false)]
    [InlineData(RomdRoleType.Admin, true)]
    public async Task SignalR_ExistingRpcArguments_EnforceMediaVisibilityOnActualConnection(RomdRoleType role, bool mediaVisible)
    {
        Guid actorId = Guid.NewGuid();
        Job media = MediaJob(false, actorId);
        var ordinary = UploadJob.Create("ordinary.zip", createdAt: DateTimeOffset.UtcNow, createdByUserId: actorId);
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdAsync(media.Id, Arg.Any<CancellationToken>()).Returns(media);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTestAuthentication();
        builder.Services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();
        builder.Services.AddSingleton(repository);
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<JobHub>("/hubs/jobs");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await app.StartAsync(deadline.Token);
        var client = app.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers[TestAuthHandler.UserIdHeader] = actorId.ToString();
            request.Headers[TestAuthHandler.RolesHeader] = role.ToString();
        };
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/hubs/jobs"), deadline.Token);
        var pendingFrames = new Queue<string>();
        try
        {
            await SendFrameAsync(socket, new { protocol = "json", version = 1 }, deadline.Token);
            using (var handshake = await ReadFrameAsync(socket, pendingFrames, deadline.Token))
                handshake.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
            await SendFrameAsync(socket, new { type = 1, invocationId = "feed", target = "SubscribeToJobFeed", arguments = Array.Empty<string>() }, deadline.Token);
            using (var completion = await ReadFrameAsync(socket, pendingFrames, deadline.Token))
            {
                completion.RootElement.GetProperty("type").GetInt32().ShouldBe(3);
                completion.RootElement.GetProperty("invocationId").GetString().ShouldBe("feed");
                completion.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
            }
            await SendFrameAsync(socket, new { type = 1, invocationId = "join", target = "SubscribeToJob", arguments = new[] { media.Id.ToString() } }, deadline.Token);
            using (var completion = await ReadFrameAsync(socket, pendingFrames, deadline.Token))
            {
                completion.RootElement.GetProperty("type").GetInt32().ShouldBe(3);
                completion.RootElement.GetProperty("invocationId").GetString().ShouldBe("join");
                completion.RootElement.TryGetProperty("error", out var error).ShouldBe(!mediaVisible);
                if (!mediaVisible)
                    error.GetString().ShouldNotBeNull().ShouldContain("Job unavailable.");
            }

            var keys = await TestReferenceCatalog.Create().GetSystemKeysAsync(deadline.Token);
            var notifier = new SignalRJobNotifier(app.Services.GetRequiredService<IHubContext<JobHub>>());
            await notifier.SendJobUpdatedAsync(media.ToContract(keys), 1, deadline.Token);
            await notifier.SendJobUpdatedAsync(ordinary.ToContract(keys), 1, deadline.Token);
            if (mediaVisible)
            {
                for (int delivery = 0; delivery < 2; delivery++)
                {
                    using var update = await ReadFrameAsync(socket, pendingFrames, deadline.Token);
                    AssertUpdatedIdentity(update, media.Id);
                }
            }
            using var barrier = await ReadFrameAsync(socket, pendingFrames, deadline.Token);
            AssertUpdatedIdentity(barrier, ordinary.Id);
        }
        finally
        {
            socket.Abort();
        }
    }

    private static void AssertUpdatedIdentity(JsonDocument message, Guid id)
    {
        message.RootElement.GetProperty("type").GetInt32().ShouldBe(1);
        message.RootElement.GetProperty("target").GetString().ShouldBe("JobUpdated");
        message.RootElement.GetProperty("arguments")[0].GetProperty("id").GetGuid().ShouldBe(id);
    }

    private static async Task SendFrameAsync<T>(WebSocket socket, T message, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + '\u001e');
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonDocument> ReadFrameAsync(WebSocket socket, Queue<string> pendingFrames, CancellationToken ct)
    {
        while (true)
        {
            if (pendingFrames.Count == 0)
            {
                using var payload = new MemoryStream();
                byte[] buffer = new byte[4096];
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                    received.MessageType.ShouldBe(WebSocketMessageType.Text);
                    payload.Write(buffer, 0, received.Count);
                    payload.Length.ShouldBeLessThanOrEqualTo(64 * 1024);
                } while (!received.EndOfMessage);
                foreach (string frame in Encoding.UTF8.GetString(payload.ToArray()).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                    pendingFrames.Enqueue(frame);
            }
            var document = JsonDocument.Parse(pendingFrames.Dequeue());
            if (!document.RootElement.TryGetProperty("type", out var type) || type.GetInt32() != 6)
                return document;
            document.Dispose();
        }
    }

    private static Job MediaJob(bool? bulk, Guid actorId)
    {
        MediaReconciliationRequest request = bulk is null
            ? new MediaReconciliationRequest.SubjectWork(new Romd.Domain.Source.Rom.MediaReconciliationSubject.Representation(1))
            : bulk.Value
                ? new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(42, 0, 0, 0), 10, false)
                : new MediaReconciliationRequest.SubjectWork(new Romd.Domain.Source.Rom.MediaReconciliationSubject.LogicalRom(1));
        return MediaReconciliationJob.Create(Guid.NewGuid(), request, "media fixture", null, actorId, DateTimeOffset.UtcNow);
    }

    private static ServiceProvider Services(IJobRepository repository, Guid actorId, RomdRoleType role)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actorId);
        user.IsAuthenticated.Returns(true);
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(call => role >= call.Arg<RomdRoleType>());
        return new ServiceCollection().AddSingleton(repository).AddSingleton(user).BuildServiceProvider();
    }

    private static JobHub CreateHub(IServiceProvider services, IGroupManager groups, Guid actorId, RomdRoleType role)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString()), new Claim(ClaimTypes.Role, role.ToString())], "Test")));
        var hub = ActivatorUtilities.CreateInstance<JobHub>(services);
        hub.Context = context;
        hub.Groups = groups;
        return hub;
    }
}
