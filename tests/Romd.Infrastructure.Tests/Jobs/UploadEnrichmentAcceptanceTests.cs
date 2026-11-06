using System.Reflection;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class UploadEnrichmentAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowUp_ProvidersPrepared_PreservesCapturedActorDefaultScopeTokenAndAmbientFence(bool hasActor)
    {
        var events = new List<string>();
        var first = Substitute.For<IMetadataProvider>();
        first.IsConfigured.Returns(true);
        first.InitializeAsync(Arg.Any<CancellationToken>()).Returns(_ => { events.Add("first-init"); return Task.CompletedTask; });
        var second = Substitute.For<IMetadataProvider>();
        second.InitializeAsync(Arg.Any<CancellationToken>()).Returns(_ => { events.Add("second-init"); return Task.CompletedTask; });
        var acceptance = Substitute.For<ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>>();
        Guid? actor = hasActor ? Guid.NewGuid() : null;
        using var fence = JobExecutionFenceScope.Enter(Guid.NewGuid(), Guid.NewGuid(), TimeProvider.System);
        var expectedFence = JobExecutionFenceScope.Current;
        acceptance.HandleAsync(Arg.Any<QueueBulkEnrichmentJobCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            events.Add("accept");
            call.Arg<QueueBulkEnrichmentJobCommand>().ShouldBe(new(3, EnrichmentScope.Tracked, actor));
            call.Arg<CancellationToken>().ShouldBe(CancellationToken.None);
            JobExecutionFenceScope.Current.ShouldBe(expectedFence);
            return Task.FromResult<ErrorOr<Guid>>(Guid.NewGuid());
        });

        await InvokeAsync(Executor(acceptance, [first, second], new RecordingLogger()), actor);

        events.ShouldBe(["first-init", "second-init", "accept"]);
        await acceptance.Received(1).HandleAsync(new(3, EnrichmentScope.Tracked, actor), CancellationToken.None);
        JobExecutionFenceScope.Current.ShouldBe(expectedFence);
    }

    [Fact]
    public async Task FollowUp_NoConfiguredProvider_DoesNotAcceptWork()
    {
        var provider = Substitute.For<IMetadataProvider>();
        var acceptance = Substitute.For<ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>>();

        await InvokeAsync(Executor(acceptance, [provider], new RecordingLogger()), null);

        await provider.Received(1).InitializeAsync(CancellationToken.None);
        acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("acceptance")]
    [InlineData("cancellation")]
    [InlineData("ownership")]
    [InlineData("rejection")]
    public async Task FollowUp_Failure_RemainsBestEffortWithoutEscaping(string failure)
    {
        var provider = Substitute.For<IMetadataProvider>();
        provider.IsConfigured.Returns(true);
        var acceptance = Substitute.For<ICommandHandler<QueueBulkEnrichmentJobCommand, Guid>>();
        var logger = new RecordingLogger();
        Exception cause = failure switch
        {
            "cancellation" => new OperationCanceledException(),
            "ownership" => new JobExecutionOwnershipLostException(),
            _ => new IOException("injected follow-up failure")
        };
        if (failure == "provider")
            provider.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(cause));
        else if (failure == "rejection")
            acceptance.HandleAsync(Arg.Any<QueueBulkEnrichmentJobCommand>(), Arg.Any<CancellationToken>())
            .Returns(Error.Validation("Enrichment.TestRejection", "injected"));
        else
            acceptance.HandleAsync(Arg.Any<QueueBulkEnrichmentJobCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<Guid>>(cause));

        await InvokeAsync(Executor(acceptance, [provider], logger), Guid.NewGuid());

        var warning = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ShouldHaveSingleItem();
        if (failure == "rejection")
        {
            warning.Exception.ShouldBeNull();
            warning.Message.ShouldContain("Enrichment.TestRejection");
        }
        else
            warning.Exception.ShouldBeSameAs(cause);
        if (failure == "provider")
            acceptance.ReceivedCalls().ShouldBeEmpty();
    }

    private static UploadJobExecutor Executor(ICommandHandler<QueueBulkEnrichmentJobCommand, Guid> acceptance,
        IEnumerable<IMetadataProvider> providers, ILogger<UploadJobExecutor> logger) =>
        new(null!, null!, null!, null!, null!, acceptance, providers, null!, logger);

    private static Task InvokeAsync(UploadJobExecutor executor, Guid? actor) => typeof(UploadJobExecutor)
        .GetMethod("TryEnqueueAutoEnrichmentAsync", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull()
        .Invoke(executor, [3, actor]).ShouldBeAssignableTo<Task>().ShouldNotBeNull();

    private sealed class RecordingLogger : ILogger<UploadJobExecutor>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
