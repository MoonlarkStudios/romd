using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Dats;

public sealed class DatReplacementReadCleanupTests
{
    private static readonly byte[] Document = Encoding.UTF8.GetBytes(
        "<datafile><header><name>Catalog</name></header><game name='A'><rom name='a.bin' size='1' crc='11111111'/></game></datafile>");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrepareAsync_ReadAndOriginalDisposalFail_PreservesExactCauseTokenAndCallerStream(bool cancel, bool failOriginal)
    {
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("original read failure");
        var cleanup = new IOException("owned original disposal failed");
        var original = new ObservedOriginal(failOriginal ? cause : null, cleanup);
        using var candidate = new ReadStream(failOriginal ? null : cause);
        var service = Service(original);

        var error = await Record.ExceptionAsync(() => service.PrepareAsync(0, candidate, new string('0', 64), new string('1', 64), cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        original.DisposeCalls.ShouldBe(1);
        original.CanRead.ShouldBeFalse();
        candidate.CanRead.ShouldBeTrue();
        if (failOriginal)
            candidate.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewAsync_OnlyOriginalDisposalFails_PropagatesCleanupForSuccessOrValidationResult(bool invalid)
    {
        var cleanup = new IOException("owned original disposal failed");
        var original = new ObservedOriginal(null, cleanup);
        using var candidate = new MemoryStream(invalid ? Encoding.UTF8.GetBytes("not XML") : Document);

        var error = await Record.ExceptionAsync(() => Service(original).PreviewAsync(0, candidate, default));

        error.ShouldBeSameAs(cleanup);
        original.DisposeCalls.ShouldBe(1);
        original.CanRead.ShouldBeFalse();
        candidate.CanRead.ShouldBeTrue();
    }

    private static DatReplacementReview Service(Stream original)
    {
        var dat = DatFile.CreateNew("Catalog", "Catalog", DatType.Redump, "catalog.dat", 1, now: DateTimeOffset.UnixEpoch);
        var repository = Substitute.For<IDatRepository>();
        repository.GetByIdAsync(0, Arg.Any<CancellationToken>()).Returns(dat);
        repository.GetActiveBySourceIdAsync(0, Arg.Any<CancellationToken>()).Returns(dat);
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream?>(original));
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        return new(repository, storage, parser, parser);
    }

    private class ReadStream(Exception? failure) : MemoryStream(Document, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            failure is null ? base.ReadAsync(buffer, ct) : ValueTask.FromException<int>(failure);
    }

    private sealed class ObservedOriginal(Exception? failure, Exception cleanup) : ReadStream(failure)
    {
        public int DisposeCalls { get; private set; }
        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await base.DisposeAsync();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }
}
