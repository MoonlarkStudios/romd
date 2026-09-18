using NSubstitute;
using Romd.Admin.Application.Diagnostics;
using Romd.Infrastructure.Diagnostics;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Diagnostics;

public sealed class HangfireDiagnosticsReaderTests
{
    [Fact]
    public async Task ReadAsync_HangfireNotRegistered_ReturnsUnavailableWithoutThrowing()
    {
        var source = Substitute.For<IHangfireDiagnosticsSource>();
        source.IsAvailable.Returns(false);

        var result = await new HangfireDiagnosticsReader(source).ReadAsync(10, 10);

        result.IsAvailable.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("not registered");
        result.Servers.Items.ShouldBeEmpty();
        result.RecurringJobs.Items.ShouldBeEmpty();
        result.QueueBacklogs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_AvailableStorage_MapsKnownQueuesAndTruncatesInventories()
    {
        var source = Substitute.For<IHangfireDiagnosticsSource>();
        source.IsAvailable.Returns(true);
        source.Read(3, 2, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<IReadOnlyList<string>>(2).ShouldBe([
                    "default",
                    "upload",
                    "enrichment",
                    "materialization"
                ]);
                return new HangfireDiagnosticsSourceData(
                    [
                        new HangfireServerDiagnosticsData("a", 1, ["default"], null, null),
                        new HangfireServerDiagnosticsData("b", 2, ["upload"], null, null),
                        new HangfireServerDiagnosticsData("c", 3, ["enrichment"], null, null)
                    ],
                    [
                        new RecurringJobDiagnosticsData("a", "default", "* * * * *", null, null, null, null),
                        new RecurringJobDiagnosticsData("b", "default", "* * * * *", null, null, null, null)
                    ],
                    [
                        new QueueBacklogDiagnosticsData("default", 4, 1),
                        new QueueBacklogDiagnosticsData("upload", 3, 2),
                        new QueueBacklogDiagnosticsData("enrichment", 2, 1),
                        new QueueBacklogDiagnosticsData("materialization", 1, 0)
                    ]);
            });

        var result = await new HangfireDiagnosticsReader(source).ReadAsync(2, 1);

        result.IsAvailable.ShouldBeTrue();
        result.Servers.IsTruncated.ShouldBeTrue();
        result.Servers.Items.Select(server => server.Name).ShouldBe(["a", "b"]);
        result.RecurringJobs.IsTruncated.ShouldBeTrue();
        result.RecurringJobs.Items.ShouldHaveSingleItem().Id.ShouldBe("a");
        result.QueueBacklogs.Select(queue => queue.Queue).ShouldBe([
            "default",
            "upload",
            "enrichment",
            "materialization"
        ]);
    }

    [Fact]
    public async Task ReadAsync_ProviderReturnsOversizedFields_ClampsEveryProviderControlledString()
    {
        string oversized = new('x', OperationalDiagnosticsPolicy.ProviderTextLimit + 1);
        string oversizedError = new('e', OperationalDiagnosticsPolicy.ErrorTextLimit + 1);
        var source = Substitute.For<IHangfireDiagnosticsSource>();
        source.IsAvailable.Returns(true);
        source.Read(2, 2, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HangfireDiagnosticsSourceData(
                [new HangfireServerDiagnosticsData(oversized, 1, [oversized], null, null)],
                [new RecurringJobDiagnosticsData(
                    oversized,
                    oversized,
                    oversized,
                    null,
                    null,
                    oversized,
                    oversizedError)],
                []));

        var result = await new HangfireDiagnosticsReader(source).ReadAsync(1, 1);

        var server = result.Servers.Items.ShouldHaveSingleItem();
        server.Name.Length.ShouldBe(OperationalDiagnosticsPolicy.ProviderTextLimit);
        server.Queues.ShouldHaveSingleItem().Length.ShouldBe(OperationalDiagnosticsPolicy.ProviderTextLimit);
        var recurring = result.RecurringJobs.Items.ShouldHaveSingleItem();
        recurring.Id.Length.ShouldBe(OperationalDiagnosticsPolicy.ProviderTextLimit);
        recurring.Queue.Length.ShouldBe(OperationalDiagnosticsPolicy.ProviderTextLimit);
        recurring.Cron.Length.ShouldBe(OperationalDiagnosticsPolicy.ProviderTextLimit);
        recurring.LastJobState!.Length.ShouldBe(OperationalDiagnosticsPolicy.ProviderTextLimit);
        recurring.Error!.Length.ShouldBe(OperationalDiagnosticsPolicy.ErrorTextLimit);
    }

    [Fact]
    public async Task ReadAsync_CancelledToken_PropagatesCancellation()
    {
        var source = Substitute.For<IHangfireDiagnosticsSource>();
        source.IsAvailable.Returns(true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new HangfireDiagnosticsReader(source).ReadAsync(10, 10, cts.Token));
    }

    [Fact]
    public async Task ReadAsync_AvailableSourceThrows_ReturnsUnavailableWithoutPartialInventories()
    {
        var source = Substitute.For<IHangfireDiagnosticsSource>();
        source.IsAvailable.Returns(true);
        source.Read(11, 11, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("monitoring unavailable"));

        var result = await new HangfireDiagnosticsReader(source).ReadAsync(10, 10);

        result.IsAvailable.ShouldBeFalse();
        result.Error.ShouldBe("monitoring unavailable");
        result.Servers.Items.ShouldBeEmpty();
        result.RecurringJobs.Items.ShouldBeEmpty();
        result.QueueBacklogs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_AvailableSourceThrowsOversizedError_ClampsUnavailableReason()
    {
        var source = Substitute.For<IHangfireDiagnosticsSource>();
        source.IsAvailable.Returns(true);
        source.Read(11, 11, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException(
                new string('e', OperationalDiagnosticsPolicy.ErrorTextLimit + 1)));

        var result = await new HangfireDiagnosticsReader(source).ReadAsync(10, 10);

        result.Error!.Length.ShouldBe(OperationalDiagnosticsPolicy.ErrorTextLimit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task ReadAsync_NonPositiveInventoryLimit_Throws(int serverLimit, int recurringJobLimit)
    {
        var source = Substitute.For<IHangfireDiagnosticsSource>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            new HangfireDiagnosticsReader(source).ReadAsync(serverLimit, recurringJobLimit));

        source.DidNotReceiveWithAnyArgs().Read(default, default, default!, default);
    }
}
