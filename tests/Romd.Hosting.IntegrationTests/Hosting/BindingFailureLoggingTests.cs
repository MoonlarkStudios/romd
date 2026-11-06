using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenIddict.Validation.AspNetCore;
using Romd.Admin.Application.Libraries.Queries.GetLibraryById;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;
using Romd.Consumer.Application.Delivery;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Identity;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     A binding failure is a client error: each host records it once, as a request-log entry with
///     the final 400, and never as an Error-level entry. Request logging wraps the shared exception
///     handler, which emits no diagnostics for BadHttpRequestException.
/// </summary>
public sealed class BindingFailureLoggingTests : IDisposable
{
    private const string TestJwtSecret = "binding-failure-logging-secret-at-least-32-characters";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-binding-failure-logging-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, "/api/titles/not-a-sqid!", true)]
    [InlineData(true, "/media/not-a-sqid!", false)]
    [InlineData(false, "/api/libraries/not-a-sqid!", true)]
    [InlineData(false, "/media/not-a-sqid!", false)]
    public async Task BindingFailure_LogsOneRequestAt400AndNoErrors(bool consumer, string path, bool authenticated)
    {
        var sink = new CapturingLogSink();
        var (factory, client) = consumer ? Start(CreateConsumerHost(sink)) : Start(CreateAdminHost(sink));
        await using var ownedFactory = factory;
        using var ownedClient = client;
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), roles: [consumer ? RomdRoleType.User : RomdRoleType.Admin]);

        using var response = await client.GetAsync(path);

        ((int)response.StatusCode).ShouldBe(400);
        var requestLog = await sink.WaitForRequestLogAsync();
        requestLog.Level.ShouldBe(LogEventLevel.Information);
        requestLog.Properties["StatusCode"].ShouldBe(new ScalarValue(400));
        sink.Events
            .Where(logEvent => logEvent.Level >= LogEventLevel.Error)
            .Select(logEvent => logEvent.RenderMessage())
            .ShouldBeEmpty();
    }

    private static (IAsyncDisposable Factory, HttpClient Client) Start<TProgram>(WebApplicationFactory<TProgram> factory)
        where TProgram : class =>
        (factory, factory.CreateClient());

    private WebApplicationFactory<Romd.Consumer.Host.Program> CreateConsumerHost(CapturingLogSink sink) =>
        RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(
            _dataDirectory,
            "Testing",
            TestJwtSecret,
            services => CapturingLogSink.Register(services, sink)
                .AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .ReplaceWith(Substitute.For<IQueryHandler<GetConsumerTitleQuery, ConsumerTitleDetailDto>>())
                .ReplaceWith(Substitute.For<IConsumerMediaArtifactResolver>()));

    private WebApplicationFactory<Romd.Admin.Host.Program> CreateAdminHost(CapturingLogSink sink) =>
        RomdHostTestFactory.CreateAdmin(
            _dataDirectory,
            TestJwtSecret,
            services => CapturingLogSink.Register(services, sink)
                .AddTestAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .ReplaceWith(Substitute.For<IQueryHandler<GetLibraryByIdQuery, LibraryDto>>()));
}
