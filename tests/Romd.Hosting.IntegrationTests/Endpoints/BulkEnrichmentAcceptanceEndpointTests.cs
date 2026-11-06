using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class BulkEnrichmentAcceptanceEndpointTests
{
    [Theory]
    [InlineData(EnrichmentScope.Tracked)]
    [InlineData(EnrichmentScope.All)]
    public async Task TriggerBulk_ResolvedSystemScopeActorAndToken_DispatchesAndReturnsCanonicalJobReference(EnrichmentScope scope)
    {
        var jobId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(actor);
        var handler = Substitute.For<ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>>();
        handler.HandleAsync(Arg.Any<QueueBulkEnrichmentJobCommand>(), cancellation.Token).Returns(jobId);
        var system = SystemKeyParameter.Parse("captured-system", null).Resolve(3);
        var method = typeof(EnrichmentEndpoints).GetMethod("TriggerBulkEnrichment", BindingFlags.NonPublic | BindingFlags.Static)
            .ShouldNotBeNull();

        var result = await method.Invoke(null, [system, handler, currentUser, cancellation.Token, scope])
            .ShouldBeOfType<Task<IResult>>();

        var accepted = result.ShouldBeOfType<Accepted<JobReference>>();
        accepted.Value.ShouldNotBeNull().JobId.ShouldBe(jobId);
        accepted.Location.ShouldBe($"/api/jobs/{jobId}");
        await handler.Received(1).HandleAsync(new(3, scope, actor), cancellation.Token);
        method.GetParameters().Last().DefaultValue.ShouldBe(EnrichmentScope.Tracked);
    }
}
