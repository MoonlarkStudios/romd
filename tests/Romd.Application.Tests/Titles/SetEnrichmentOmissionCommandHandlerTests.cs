using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Enrichment;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class SetEnrichmentOmissionCommandHandlerTests
{
    private readonly IEnrichmentWorkbench _workbench = Substitute.For<IEnrichmentWorkbench>();
    private readonly ITitleDetailReader _details = Substitute.For<ITitleDetailReader>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly SetEnrichmentOmissionCommand _command = new(1, "Description", true, RevisionPrecondition.For(Guid.NewGuid().ToString()));

    public SetEnrichmentOmissionCommandHandlerTests()
    {
        _work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _workbench.SetOmissionAsync(1, "Description", true, _command.IfMatch, Arg.Any<CancellationToken>()).Returns(Result.Updated);
    }

    [Fact]
    public async Task HandleAsync_Success_FlushesBeforeReadingItsResponseAndCommitsOnce()
    {
        var order = new List<string>();
        var revision = Guid.NewGuid();
        _work.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Add("flush"); return Task.CompletedTask; });
        _details.GetDetailAsync(1, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("read");
            return Detail(revision);
        });
        _transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Add("commit"); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();

        var result = await Handler().HandleAsync(_command, cancellation.Token);

        result.IsError.ShouldBeFalse();
        result.Value.Revision.ShouldBe(revision.ToString());
        order.ShouldBe(["flush", "read", "commit"]);
        await _work.Received(1).FlushAsync(cancellation.Token);
        await _transaction.Received(1).CommitAsync(cancellation.Token);
        await _transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_RejectedDecision_DoesNotFlushReadOrCommit()
    {
        var error = Error.Conflict("Enrichment.FieldPresent", "This field now has a value.");
        _workbench.SetOmissionAsync(1, "Description", true, _command.IfMatch, Arg.Any<CancellationToken>()).Returns((ErrorOr<Updated>)error);

        var result = await Handler().HandleAsync(_command);

        result.Errors.ShouldBe([error]);
        await _work.DidNotReceiveWithAnyArgs().FlushAsync(default);
        _details.ReceivedCalls().ShouldBeEmpty();
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await _transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_FlushFails_DisposesWithoutReadingOrCommitting()
    {
        var failure = new InvalidOperationException("Injected flush failure.");
        _work.FlushAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));

        (await Should.ThrowAsync<InvalidOperationException>(() => Handler().HandleAsync(_command))).ShouldBeSameAs(failure);

        _details.ReceivedCalls().ShouldBeEmpty();
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await _transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_ResponseMissingAfterFlush_ReturnsNotFoundWithoutCommit()
    {
        var result = await Handler().HandleAsync(_command);

        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await _work.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        await _transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await _transaction.Received(1).DisposeAsync();
    }

    private SetEnrichmentOmissionCommandHandler Handler() => new(TestSystemCatalog.Create(), _workbench, _details, _work, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetEnrichmentOmissionCommandHandler>.Instance);
    private static TitleDetailData Detail(Guid revision) => new()
    {
        Id = 1,
        Revision = revision,
        PlatformId = 1,
        Name = "Title",
        EnrichmentStatus = "None",
        IsTracked = true
    };
}
