using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Persistence;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Catalog;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class RomCdImportCatalogReaderTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private static readonly RomLogicalIdentity Bin = MediaFactFixtures.Identity(new byte[2352 * 4]);
    private static readonly RomLogicalIdentity Cue = MediaFactFixtures.Identity("original CUE"u8);
    public async Task InitializeAsync()
    {
        await using var db = database.CreateContext();
        db.Platforms.Add(new()
        {
            Id = 1,
            Name = "Fixture",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Fixture",
            BaseCompactLabel = "FX",
            ShortName = "registered",
            CanonicalKey = "registered"
        });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Fixture", NormalizedName = "fixture", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Name = "Fixture", Fingerprint = "release" });
        db.CatalogReleaseFiles.AddRange(
            new() { Id = 1, CatalogReleaseId = 1, FileFingerprint = "bin", Name = "track.bin", Size = Bin.Size, Sha1 = Bin.Sha1, Md5 = Bin.Md5, Crc = Bin.Crc32 },
            new() { Id = 2, CatalogReleaseId = 1, FileFingerprint = "cue", Name = "disc.cue", Size = Cue.Size, Sha1 = Cue.Sha1, Md5 = Cue.Md5, Crc = Cue.Crc32 });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task Find_TrackHashMatches_ReturnsTheCompleteRequirementSetWithoutOwningATransaction()
    {
        await using var db = database.CreateContext();
        var entry = (await new RomCdImportCatalogReader(db).FindAsync([Bin], ["registered"], CancellationToken.None)).Single();
        db.Database.CurrentTransaction.ShouldBeNull();
        entry.Files.Select(file => file.Name).ShouldBe(["track.bin", "disc.cue"]);
        entry.Files[1].Sha1.ShouldBe(Cue.Sha1);
        entry.Files[1].Md5.ShouldBe(Cue.Md5);
        entry.Files[1].Crc32.ShouldBe(Cue.Crc32);
        var binding = RomCdImportBindings.Select([new(1, Bin)], [entry], ["registered"]);
        binding.IsError.ShouldBeFalse();
        binding.Value.Cue.CatalogFileId.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Find_ForeignScopeOrConflictedTitle_DoesNotReturnTheEntry(bool conflict)
    {
        await using var db = database.CreateContext();
        if (conflict)
            await db.CatalogReleases.ExecuteUpdateAsync(set => set.SetProperty(row => row.HasTitleConflict, true));
        (await new RomCdImportCatalogReader(db).FindAsync([Bin], [conflict ? "registered" : "other"], CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Find_CallerPublicationTransaction_PreservesItsOwnershipAndIncludesItsUncommittedFacts()
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Romd.Persistence.CatalogTopologyFence.AcquireAsync(db, CancellationToken.None);
        await db.CatalogReleaseFiles.Where(file => file.Id == 2).ExecuteUpdateAsync(set => set.SetProperty(file => file.Name, "updated.cue"));
        var entry = (await new RomCdImportCatalogReader(db).FindAsync([Bin], ["registered"], CancellationToken.None)).Single();
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        entry.Files.Single(file => file.Id == 2).Name.ShouldBe("updated.cue");
        await transaction.RollbackAsync();
        await using var observer = database.CreateContext();
        (await observer.CatalogReleaseFiles.SingleAsync(file => file.Id == 2)).Name.ShouldBe("disc.cue");
    }

    [Fact]
    public async Task Find_AdditionalSidecar_IsRetainedAsARequirementAndNeverSilentlyOmitted()
    {
        await using var db = database.CreateContext();
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "sidecar", Name = "disc.sbi", Size = 4 });
        await db.SaveChangesAsync();
        var entry = (await new RomCdImportCatalogReader(db).FindAsync([Bin], ["registered"], CancellationToken.None)).Single();
        entry.Files.Count.ShouldBe(3);
        RomCdImportBindings.Select([new(1, Bin)], [entry], ["registered"]).FirstError.Code.ShouldBe("Rom.CdImport.UnsupportedLayout");
    }

    [Fact]
    public async Task Find_EntryRequirementOverflow_RemainsVisibleToTheBinderAsAnOverflowSentinel()
    {
        await using var db = database.CreateContext();
        for (int i = 0; i < 120; i++)
            db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "sidecar-" + i, Name = "sidecar-" + i, Size = 4 });
        await db.SaveChangesAsync();
        var entries = await new RomCdImportCatalogReader(db).FindAsync([Bin], ["registered"], CancellationToken.None);
        entries.Single().Files.Count.ShouldBe(117);
        RomCdImportBindings.Select([new(1, Bin)], entries, ["registered"]).FirstError.Code.ShouldBe("Rom.CdImport.UnsupportedLayout");
    }

    [Fact]
    public async Task Find_CandidateOverflow_RemainsVisibleInsteadOfClaimingAUniqueTruncatedMatch()
    {
        await using var db = database.CreateContext();
        for (int id = 2; id <= 260; id++)
        {
            db.CatalogReleases.Add(new() { Id = id, PlatformId = 1, CatalogTitleId = 1, Name = "Candidate " + id, Fingerprint = "release-" + id });
            db.CatalogReleaseFiles.Add(new()
            {
                CatalogReleaseId = id,
                FileFingerprint = "bin-" + id,
                Name = "track.bin",
                Size = Bin.Size,
                Sha1 = Bin.Sha1,
                Md5 = Bin.Md5,
                Crc = Bin.Crc32
            });
        }
        await db.SaveChangesAsync();
        var entries = await new RomCdImportCatalogReader(db).FindAsync([Bin], ["registered"], CancellationToken.None);
        entries.Count.ShouldBe(257);
        RomCdImportBindings.Select([new(1, Bin)], entries, ["registered"]).FirstError.Code.ShouldBe("Rom.CdImport.UnsupportedLayout");
    }


    [Fact]
    public async Task Find_UnfencedReadCommittedWithConcurrentCatalogReplacement_ReturnsOneConsistentStatementSnapshot()
    {
        var pause = new PauseAfterFirstCatalogStatement();
        await using var reader = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(pause)));
        await using var transaction = await reader.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var reading = new RomCdImportCatalogReader(reader).FindAsync([Bin], ["registered"], CancellationToken.None);
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var writer = database.CreateContext();
            await using var replacement = await writer.Database.BeginTransactionAsync();
            await CatalogTopologyFence.AcquireAsync(writer, CancellationToken.None);
            await writer.CatalogReleases.Where(release => release.Id == 1).ExecuteUpdateAsync(set => set.SetProperty(release => release.HasTitleConflict, true));
            await writer.CatalogReleaseFiles.Where(file => file.Id == 2).ExecuteUpdateAsync(set => set.SetProperty(file => file.Name, "changed.cue"));
            writer.CatalogReleases.Add(new() { Id = 2, PlatformId = 1, CatalogTitleId = 1, Name = "Replacement", Fingerprint = "replacement" });
            writer.CatalogReleaseFiles.AddRange(
                new() { Id = 3, CatalogReleaseId = 2, FileFingerprint = "replacement-bin", Name = "replacement.bin", Size = Bin.Size, Sha1 = Bin.Sha1, Md5 = Bin.Md5, Crc = Bin.Crc32 },
                new() { Id = 4, CatalogReleaseId = 2, FileFingerprint = "replacement-cue", Name = "replacement.cue", Size = Cue.Size, Sha1 = Cue.Sha1, Md5 = Cue.Md5, Crc = Cue.Crc32 });
            await writer.SaveChangesAsync();
            await replacement.CommitAsync();
        }
        finally { pause.Resume.TrySetResult(); }
        var entries = await reading;
        entries.Single().ReleaseId.ShouldBe(1);
        entries.Single().Files.Single(file => file.Id == 2).Name.ShouldBe("disc.cue");
        pause.CatalogStatements.ShouldBe(1);
        reader.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        await transaction.RollbackAsync();
        await using var observer = database.CreateContext();
        var current = (await new RomCdImportCatalogReader(observer).FindAsync([Bin], ["registered"], CancellationToken.None)).Single();
        current.ReleaseId.ShouldBe(2);
        current.Files.Single(file => file.Id == 4).Name.ShouldBe("replacement.cue");
    }

    private sealed class PauseAfterFirstCatalogStatement : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CatalogStatements { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CatalogReleaseFiles", StringComparison.Ordinal) && ++CatalogStatements == 1)
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

}
