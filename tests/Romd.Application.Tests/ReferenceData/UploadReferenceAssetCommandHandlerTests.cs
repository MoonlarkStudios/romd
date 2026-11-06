using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.ReferenceData.Assets;
using Romd.Admin.Application.ReferenceData.Assets.Commands.UploadReferenceAsset;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.ReferenceData;

public sealed class UploadReferenceAssetCommandHandlerTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddTicks(1234567);

    public static IEnumerable<object[]> InvalidUploads()
    {
        yield return [new byte[0], "image/png", "Icons must be at most 2 MiB."];
        yield return [Png[..11], "image/png", "Icons must be at most 2 MiB."];
        yield return [new byte[2097153], "image/png", "Icons must be at most 2 MiB."];
        yield return ["<svg><script/></svg>"u8.ToArray(), "image/svg+xml", "Use a PNG or WebP icon."];
        yield return [Png, "IMAGE/PNG", "Use a PNG or WebP icon."];
        yield return [Png, "image/png; charset=binary", "Use a PNG or WebP icon."];
        yield return [Png, "image/webp", "Use a PNG or WebP icon."];
        yield return [new byte[12], "image/png", "Use a PNG or WebP icon."];
        yield return ["RIFFxxxxFAIL"u8.ToArray(), "image/webp", "Use a PNG or WebP icon."];
    }

    [Theory]
    [MemberData(nameof(InvalidUploads))]
    public async Task Upload_InvalidRaster_ReturnsExistingErrorBeforePreparationOrTransaction(byte[] bytes, string type, string message)
    {
        var fixture = new Fixture();

        var result = await fixture.Handler.HandleAsync(new UploadReferenceAssetCommand(bytes, type));

        result.FirstError.Code.ShouldBe("ReferenceCatalog.Invalid");
        result.FirstError.Description.ShouldBe(message);
        fixture.Events.ShouldBeEmpty();
        fixture.Clock.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_NewOrDeduplicatedFile_OwnsConditionalIdentityFlushAndPrecommitCleanup(bool deduplicated)
    {
        var fixture = new Fixture(deduplicated);

        var result = await fixture.Handler.HandleAsync(new UploadReferenceAssetCommand(Png, "image/png"));

        result.Value.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Png)));
        fixture.Events.ShouldBe(deduplicated
            ? ["prepare", "begin", "catalog-lock", "publish", "identity-read", "dispose-stage", "reserve", "commit", "dispose-tx"]
            : ["prepare", "begin", "catalog-lock", "publish", "flush", "identity-read", "dispose-stage", "reserve", "commit", "dispose-tx"]);
        fixture.Reservations.FileId.ShouldBe(42);
        fixture.Reservations.ContentType.ShouldBe("image/png");
        fixture.Reservations.ReservedUntil.ShouldBe(Now.AddDays(7));
        fixture.Clock.Reads.ShouldBe(1);
        fixture.Staged.DisposeCount.ShouldBe(1);
        fixture.Work.Committed.ShouldBeTrue();
        await fixture.Files.Received(1).PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_WebpOrMaximumPng_PreservesExactBytesAndContentType(bool maximumPng)
    {
        var bytes = maximumPng ? new byte[2097152] : "RIFFxxxxWEBP"u8.ToArray();
        if (maximumPng)
            Png.CopyTo(bytes, 0);
        string type = maximumPng ? "image/png" : "image/webp";
        var fixture = new Fixture(bytes: bytes);

        var result = await fixture.Handler.HandleAsync(new UploadReferenceAssetCommand(bytes, type));

        result.IsError.ShouldBeFalse();
        fixture.PreparedBytes.ShouldBe(bytes);
        fixture.Reservations.ContentType.ShouldBe(type);
    }

    [Fact]
    public async Task Upload_MutableCallerBytes_PreparesOwnedValidatedSnapshot()
    {
        var bytes = Png.ToArray();
        var fixture = new Fixture();
        fixture.BeforeRead = () => Array.Fill<byte>(bytes, 0);

        var result = await fixture.Handler.HandleAsync(new UploadReferenceAssetCommand(bytes, "image/png"));

        fixture.PreparedBytes.ShouldBe(Png);
        result.Value.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Png)));
        bytes.ShouldAllBe(value => value == 0);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("catalog-lock")]
    [InlineData("publish")]
    [InlineData("flush")]
    [InlineData("dispose-stage")]
    [InlineData("reserve")]
    [InlineData("commit")]
    public async Task Upload_BoundaryFailure_DisposesStagingOnceAndNeverCommits(string failAt)
    {
        var fixture = new Fixture { FailAt = failAt };

        await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(new UploadReferenceAssetCommand(Png, "image/png")));

        fixture.Work.Committed.ShouldBeFalse();
        fixture.Staged.DisposeCount.ShouldBe(1);
        if (failAt == "dispose-stage")
        {
            fixture.Events.ShouldNotContain("reserve");
            fixture.Logger.Warnings.ShouldBeEmpty();
        }
        if (failAt != "begin")
            fixture.Events.ShouldContain("dispose-tx");
    }

    [Fact]
    public async Task Upload_CancellationAfterIdentityFlush_PropagatesTokenAndDisposesStagingAndTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { CancelAfterFlush = cancellation };

        var error = await Should.ThrowAsync<OperationCanceledException>(() => fixture.Handler.HandleAsync(
            new UploadReferenceAssetCommand(Png, "image/png"), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        fixture.Work.Committed.ShouldBeFalse();
        fixture.Staged.DisposeCount.ShouldBe(1);
        fixture.Events.ShouldNotContain("reserve");
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("catalog-lock")]
    [InlineData("publish")]
    [InlineData("flush")]
    public async Task Upload_PrimaryFailureAndCleanupFailure_PreservesOriginalExceptionAndLogsCleanup(string failAt)
    {
        var cleanup = new IOException("Secondary staging cleanup failure.");
        var fixture = new Fixture { FailAt = failAt, CleanupFailure = cleanup };

        var error = await Should.ThrowAsync<IOException>(() => fixture.Handler.HandleAsync(new(Png, "image/png")));

        error.ShouldBeSameAs(fixture.PrimaryFailure);
        fixture.Logger.Warnings.ShouldHaveSingleItem().ShouldBeSameAs(cleanup);
        fixture.Work.Committed.ShouldBeFalse();
        fixture.Staged.DisposeCount.ShouldBe(1);
        fixture.Events.ShouldNotContain("reserve");
    }

    [Fact]
    public async Task Upload_CallerCancellationAndCleanupFailure_PreservesTokenAndLogsCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new IOException("Secondary staging cleanup failure.");
        var fixture = new Fixture { CancelAfterFlush = cancellation, CleanupFailure = cleanup };

        var error = await Should.ThrowAsync<OperationCanceledException>(() => fixture.Handler.HandleAsync(new(Png, "image/png"), cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        fixture.Logger.Warnings.ShouldHaveSingleItem().ShouldBeSameAs(cleanup);
        fixture.Work.Committed.ShouldBeFalse();
        fixture.Staged.DisposeCount.ShouldBe(1);
        fixture.Events.ShouldNotContain("reserve");
    }

    private sealed class Fixture
    {
        public List<string> Events { get; } = [];
        public string? FailAt { get; init; }
        public IOException PrimaryFailure { get; } = new("Primary acceptance failure.");
        public IOException? CleanupFailure { get; init; }
        public CapturingLogger Logger { get; } = new();
        public Action? BeforeRead { get; set; }
        public CancellationTokenSource? CancelAfterFlush { get; init; }
        public byte[]? PreparedBytes { get; private set; }
        public IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        public CountingClock Clock { get; } = new();
        public StagedFile Staged { get; }
        public Work Work { get; }
        public Reservations Reservations { get; }
        public UploadReferenceAssetCommandHandler Handler { get; }

        public Fixture(bool deduplicated = false, byte[]? bytes = null)
        {
            var hash = Sha256.FromSpan(SHA256.HashData(bytes ?? Png));
            Staged = new StagedFile(this, hash);
            Work = new Work(this);
            Reservations = new Reservations(this);
            var result = new FileStoreResult(FileEntity.Rehydrate(42, hash, (bytes ?? Png).Length,
                (bytes ?? Png).Length, false, Now), !deduplicated, deduplicated);
            var publication = new Publication(this, result, requiresFlush: !deduplicated);
            Files.StageAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                BeforeRead?.Invoke();
                using var output = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(output, call.Arg<CancellationToken>());
                PreparedBytes = output.ToArray();
                Step("prepare");
                return (IStagedFile)Staged;
            });
            Files.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Step("publish", call.Arg<CancellationToken>());
                return Task.FromResult<IStagedFilePublication>(publication);
            });
            Handler = new UploadReferenceAssetCommandHandler(Reservations, Files, Work, Clock, Logger);
        }

        public void Step(string name, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(name);
            if (FailAt == name)
                throw PrimaryFailure;
        }
    }

    private sealed class CapturingLogger : ILogger<UploadReferenceAssetCommandHandler>
    {
        public List<Exception?> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(exception);
        }
    }

    private sealed class CountingClock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }

    private sealed class StagedFile(Fixture fixture, Sha256 hash) : IStagedFile
    {
        public Sha256 Sha256 => hash;
        public long Size => Png.Length;
        public Md5? ArtifactMd5 => null;
        public Sha1? ArtifactSha1 => null;
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            fixture.Step("dispose-stage");
            if (fixture.CleanupFailure is { } failure)
                throw failure;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Publication(Fixture fixture, FileStoreResult result, bool requiresFlush) : IStagedFilePublication
    {
        public bool RequiresFlush => requiresFlush && !fixture.Work.Flushed;
        public FileStoreResult ReadAfterFlush()
        {
            RequiresFlush.ShouldBeFalse();
            fixture.Step("identity-read");
            return result;
        }
    }

    private sealed class Reservations(Fixture fixture) : IReferenceAssetReservationStore
    {
        public int FileId { get; private set; }
        public string? ContentType { get; private set; }
        public DateTimeOffset ReservedUntil { get; private set; }
        public Task AcquireAsync(CancellationToken ct) { fixture.Step("catalog-lock", ct); return Task.CompletedTask; }
        public Task ReserveStagedAsync(Sha256 hash, int fileId, string contentType, DateTimeOffset reservedUntil, CancellationToken ct)
        {
            fixture.Staged.DisposeCount.ShouldBe(1);
            fixture.Step("reserve", ct);
            FileId = fileId;
            ContentType = contentType;
            ReservedUntil = reservedUntil;
            return Task.CompletedTask;
        }
    }

    private sealed class Work(Fixture fixture) : IUnitOfWork, ITransaction
    {
        public bool Flushed { get; private set; }
        public bool Committed { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => TransactionConflict.None;
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            fixture.Step("begin", cancellationToken);
            return Task.FromResult<ITransaction>(this);
        }
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            fixture.Step("flush", cancellationToken);
            Flushed = true;
            if (fixture.CancelAfterFlush is { } cancellation)
                await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            fixture.Step("commit", cancellationToken);
            Committed = true;
            return Task.CompletedTask;
        }
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { fixture.Events.Add("dispose-tx"); return ValueTask.CompletedTask; }
    }
}
