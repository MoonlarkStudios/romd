using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Platform;

public sealed class SetMetadataPolicyCommandHandlerTests
{
    private static readonly string[] Priority = ["igdb", "dat"];
    private readonly IPlatformRepository _platforms = Substitute.For<IPlatformRepository>();
    private readonly IPlatformFieldDefaultRepository _defaults = Substitute.For<IPlatformFieldDefaultRepository>();
    private readonly IMetadataRematerializationJobRepository _jobs = Substitute.For<IMetadataRematerializationJobRepository>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly Dictionary<string, string> _saved = new() { ["Description"] = "igdb", ["Genre"] = "dat" };

    public SetMetadataPolicyCommandHandlerTests()
    {
        _platforms.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Romd.Domain.Source.Platform.Platform.Rehydrate(1, "NES", "nes", null, DateTimeOffset.UnixEpoch));
        _defaults.GetByPlatformIdAsync(1, Arg.Any<CancellationToken>()).Returns(_saved);
        _work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _jobs.GetReusablePendingAsync(1, Arg.Any<CancellationToken>())
            .Returns(MetadataRematerializationJob.Create(1, "nes", null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task HandleAsync_ChangedFields_ClearsAllThenFlushesOnceBeforeSettingReplacements()
    {
        var order = new List<string>();
        _defaults.When(repo => repo.LockAsync(1, Arg.Any<CancellationToken>())).Do(_ => order.Add("lock"));
        _defaults.When(repo => repo.ClearStagedAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => order.Add("clear:" + call.Arg<string>()));
        _work.When(work => work.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("flush"));
        _defaults.When(repo => repo.SetStagedAsync(1, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => order.Add($"set:{call.ArgAt<string>(1)}:{call.ArgAt<string>(2)}"));
        _jobs.When(repo => repo.GetReusablePendingAsync(1, Arg.Any<CancellationToken>())).Do(_ => order.Add("job"));
        _transaction.When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("commit"));

        var result = await Handler().HandleAsync(new(1, Revision(), new Dictionary<string, string?>
        {
            ["Description"] = "dat",
            ["Genre"] = null,
            ["Publisher"] = "user"
        }));

        result.IsError.ShouldBeFalse();
        order.ShouldBe(["lock", "clear:Description", "clear:Genre", "clear:Publisher", "flush",
            "set:Description:dat", "set:Publisher:user", "job", "commit"]);
        await _work.Received(1).FlushAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_NoOpOrStale_DoesNotStageOrFlush(bool stale)
    {
        var result = await Handler().HandleAsync(new(1,
            stale ? RevisionPrecondition.For("stale") : Revision(),
            new Dictionary<string, string?> { ["Description"] = "igdb" }));

        result.IsError.ShouldBe(stale);
        if (stale)
            result.FirstError.Code.ShouldBe("MetadataPolicy.PreconditionFailed");
        await _work.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
        await _defaults.DidNotReceive().ClearStagedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _defaults.DidNotReceive().SetStagedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DeletionFlushFails_DoesNotStageReplacementsOrCommit()
    {
        _work.FlushAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("Injected flush failure.")));

        var result = await Handler().HandleAsync(new(1, Revision(), new Dictionary<string, string?> { ["Description"] = "dat" }));

        result.FirstError.Code.ShouldBe("MetadataPolicy.SaveFailed");
        await _defaults.DidNotReceive().SetStagedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _jobs.DidNotReceive().GetReusablePendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(1).DisposeAsync();
    }

    private RevisionPrecondition Revision() => RevisionPrecondition.For(MetadataPolicy.Revision(_saved, Priority));

    private SetMetadataPolicyCommandHandler Handler() => new(_platforms, _defaults, _jobs,
        Substitute.For<IAdminEventOutbox>(), Substitute.For<IEnrichmentJobEnqueuer>(), Substitute.For<ICurrentUser>(),
        TestSystemCatalog.Create(), Options.Create(new EnrichmentOptions { GlobalSourcePriority = Priority.ToList() }),
        _work, NullLogger<SetMetadataPolicyCommandHandler>.Instance, TimeProvider.System);
}
