using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenIddict.EntityFrameworkCore;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ConsumerReleaseAccessRepositoryTests : IAsyncDisposable
{
    private const int AllowedReleaseId = 100;
    private const int UnownedReleaseId = 200;
    private const int UnexposedReleaseId = 300;
    private const int OtherLibraryReleaseId = 400;
    private static readonly Guid FirstUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SecondUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid InvalidUserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid PendingUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid UnassignedUserId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;
    private readonly int _firstLibraryId;
    private readonly int _secondLibraryId;

    public ConsumerReleaseAccessRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .UseOpenIddict()
            .Options;

        using var context = CreateContext();
        (_firstLibraryId, _secondLibraryId) = Seed(context);
    }

    [Fact]
    public async Task GetAccessAsync_OwnedAndExposedRelease_ReturnsAllowWithTitleIdentity()
    {
        var read = await ReadAsync(FirstUserId, AllowedReleaseId);

        var found = read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>();
        found.LibraryId.ShouldBe(_firstLibraryId);
        found.Value.ShouldBe(ConsumerReleaseAccessDecision.Allow(1));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(UnownedReleaseId)]
    [InlineData(UnexposedReleaseId)]
    [InlineData(OtherLibraryReleaseId)]
    public async Task GetAccessAsync_NotAllowedInCurrentLibrary_ReturnsMetadataFreeRevoke(int releaseId)
    {
        var read = await ReadAsync(FirstUserId, releaseId);

        var found = read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>();
        found.Value.ShouldBe(ConsumerReleaseAccessDecision.Revoke());
        found.Value.TitleId.ShouldBeNull();
    }

    [Fact]
    public async Task GetAccessAsync_TwoLibrariesDifferForSameRelease()
    {
        var first = await ReadAsync(FirstUserId, AllowedReleaseId);
        var second = await ReadAsync(SecondUserId, AllowedReleaseId);

        first.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>()
            .Value.ShouldBe(ConsumerReleaseAccessDecision.Allow(1));
        second.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>()
            .Value.ShouldBe(ConsumerReleaseAccessDecision.Revoke());
    }

    [Fact]
    public async Task GetAccessAsync_SamePersistedUserAfterReassignment_UsesNewLibrary()
    {
        var before = await ReadAsync(FirstUserId, AllowedReleaseId);
        await using (var context = CreateContext())
        {
            await context.Users
                .Where(user => user.Id == FirstUserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    user => user.LibraryId,
                    _secondLibraryId));
        }
        var after = await ReadAsync(FirstUserId, AllowedReleaseId);

        before.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>()
            .Value.ShouldBe(ConsumerReleaseAccessDecision.Allow(1));
        var afterFound = after.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>();
        afterFound.LibraryId.ShouldBe(_secondLibraryId);
        afterFound.Value.ShouldBe(ConsumerReleaseAccessDecision.Revoke());
    }

    [Theory]
    [MemberData(nameof(UnavailableUsers))]
    public async Task GetAccessAsync_NoCurrentValidMaterializedLibrary_ReturnsNoAuthoritativeRead(Guid userId)
    {
        var read = await ReadAsync(userId, AllowedReleaseId);

        read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.LibraryUnavailable>();
    }

    [Fact]
    public async Task GetAccessAsync_DuplicateProjection_ReturnsInconsistentInsteadOfChoosingDecision()
    {
        await using (var context = CreateContext())
        {
            context.MaterializedLibraryReleases.Add(NewRelease(
                _firstLibraryId,
                titleId: 1,
                catalogReleaseId: AllowedReleaseId,
                datGameId: 99,
                isOwned: true,
                isExposed: true));
            await context.SaveChangesAsync();
        }

        var read = await ReadAsync(FirstUserId, AllowedReleaseId);

        read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetAccessAsync_OrphanedCatalogRelease_ReturnsInconsistentInsteadOfRevokeOrAllow()
    {
        await using (var context = CreateContext())
        {
            await context.CatalogReleases
                .Where(release => release.Id == AllowedReleaseId)
                .ExecuteDeleteAsync();
        }

        var read = await ReadAsync(FirstUserId, AllowedReleaseId);

        read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetAccessAsync_TitleMismatchedCatalogRelease_ReturnsInconsistentInsteadOfRevokeOrAllow()
    {
        await using (var context = CreateContext())
        {
            await context.CatalogReleases
                .Where(release => release.Id == AllowedReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.CatalogTitleId, 2));
        }

        var read = await ReadAsync(FirstUserId, AllowedReleaseId);

        read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetAccessAsync_PlatformMismatchedCatalogRelease_ReturnsInconsistentInsteadOfRevokeOrAllow()
    {
        await using (var context = CreateContext())
        {
            await context.CatalogReleases
                .Where(release => release.Id == AllowedReleaseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.PlatformId, 2));
        }

        var read = await ReadAsync(FirstUserId, AllowedReleaseId);

        read.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent>();
    }

    [Fact]
    public async Task GetAccessAsync_AssignmentChangesAfterResolution_ReturnsOneCoherentSnapshot()
    {
        using var database = PostgreSqlTestDatabase.Create();

        try
        {
            var setupOptions = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(database.ConnectionString)
                .UseOpenIddict()
                .Options;
            Guid userId = Guid.NewGuid();
            int firstLibraryId;
            int secondLibraryId;

            await using (var setup = new RomdDbContext(setupOptions))
            {
                await setup.Database.MigrateAsync();
                (firstLibraryId, secondLibraryId) = SeedCoherenceScenario(setup, userId);
                await setup.SaveChangesAsync();
            }

            var interceptor = new PauseAfterLiveAssignmentResolutionInterceptor();
            var readOptions = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(database.ConnectionString)
                .UseOpenIddict()
                .AddInterceptors(interceptor)
                .Options;
            await using var readContext = new RomdDbContext(readOptions);
            var repository = new ConsumerReleaseAccessRepository(readContext);
            Task<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>> readTask = repository.GetAccessAsync(
                new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(userId), AllowedReleaseId));

            await interceptor.WaitUntilResolvedAsync();
            try
            {
                await using var writer = new RomdDbContext(setupOptions);
                int updated = await writer.Users
                    .Where(user => user.Id == userId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        user => user.LibraryId,
                        secondLibraryId));
                updated.ShouldBe(1);
            }
            finally
            {
                interceptor.Resume();
            }

            var concurrentRead = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
            var concurrentFound = concurrentRead
                .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>();
            concurrentFound.LibraryId.ShouldBe(firstLibraryId);
            concurrentFound.Value.ShouldBe(ConsumerReleaseAccessDecision.Allow(1));

            await using var nextContext = new RomdDbContext(setupOptions);
            var nextRead = await new ConsumerReleaseAccessRepository(nextContext).GetAccessAsync(
                new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(userId), AllowedReleaseId));
            var nextFound = nextRead.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found>();
            nextFound.LibraryId.ShouldBe(secondLibraryId);
            nextFound.Value.ShouldBe(ConsumerReleaseAccessDecision.Revoke());
        }
        finally
        {
        }
    }

    public static TheoryData<Guid> UnavailableUsers =>
        new()
        {
            InvalidUserId,
            PendingUserId,
            UnassignedUserId,
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")
        };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private RomdDbContext CreateContext() => new(_options);

    private async Task<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>> ReadAsync(
        Guid userId,
        int releaseId)
    {
        await using var context = CreateContext();
        return await new ConsumerReleaseAccessRepository(context).GetAccessAsync(
            new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(userId), releaseId));
    }

    private static (int FirstLibraryId, int SecondLibraryId) Seed(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var first = LibraryEntity.FromDomain(Library.CreateNew("First", new LibraryConfiguration()));
        var second = LibraryEntity.FromDomain(Library.CreateNew("Second", new LibraryConfiguration()));
        var invalid = LibraryEntity.FromDomain(Library.CreateNew("Invalid", new LibraryConfiguration()));
        var pending = LibraryEntity.FromDomain(Library.CreateNew("Pending", new LibraryConfiguration()));
        first.NeedsMaterialization = false;
        second.NeedsMaterialization = false;
        invalid.NeedsMaterialization = false;
        invalid.ConfigurationState = LibraryConfigurationState.Invalid.ToString();
        invalid.ConfigurationError = "Invalid test configuration.";
        pending.NeedsMaterialization = true;
        context.Libraries.AddRange(first, second, invalid, pending);
        context.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            CreatedAt = now
        });
        context.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Nintendo Entertainment System",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo Entertainment System", BaseCompactLabel = "Nintendo Entertainment System", CanonicalKey = "nes", ShortName = "nes",
            CreatedAt = now
        });
        context.Titles.AddRange(
            new TitleEntity
            {
                Id = 1,
                PlatformId = 1,
                Name = "Allowed Title",
                NormalizedName = "allowed title",
                EnrichmentStatus = "Completed",
                CreatedAt = now
            },
            new TitleEntity
            {
                Id = 2,
                PlatformId = 1,
                Name = "Different Title",
                NormalizedName = "different title",
                EnrichmentStatus = "Completed",
                CreatedAt = now
            });
        context.CatalogReleases.AddRange(
            NewCatalogRelease(AllowedReleaseId, 1, 1, now),
            NewCatalogRelease(UnownedReleaseId, 1, 1, now),
            NewCatalogRelease(UnexposedReleaseId, 1, 1, now),
            NewCatalogRelease(OtherLibraryReleaseId, 1, 1, now));
        context.SaveChanges();

        context.Users.AddRange(
            NewUser(FirstUserId, "first-user", first.Id, now),
            NewUser(SecondUserId, "second-user", second.Id, now),
            NewUser(InvalidUserId, "invalid-user", invalid.Id, now),
            NewUser(PendingUserId, "pending-user", pending.Id, now),
            NewUser(UnassignedUserId, "unassigned-user", null, now));
        context.MaterializedLibraryReleases.AddRange(
            NewRelease(first.Id, 1, AllowedReleaseId, 1, isOwned: true, isExposed: true),
            NewRelease(second.Id, 1, AllowedReleaseId, 2, isOwned: false, isExposed: true),
            NewRelease(first.Id, 1, UnownedReleaseId, 3, isOwned: false, isExposed: true),
            NewRelease(first.Id, 1, UnexposedReleaseId, 4, isOwned: true, isExposed: false),
            NewRelease(second.Id, 1, OtherLibraryReleaseId, 5, isOwned: true, isExposed: true));
        context.SaveChanges();

        return (first.Id, second.Id);
    }

    private static (int FirstLibraryId, int SecondLibraryId) SeedCoherenceScenario(
        RomdDbContext context,
        Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var first = LibraryEntity.FromDomain(Library.CreateNew("First", new LibraryConfiguration()));
        var second = LibraryEntity.FromDomain(Library.CreateNew("Second", new LibraryConfiguration()));
        first.NeedsMaterialization = false;
        second.NeedsMaterialization = false;
        context.Libraries.AddRange(first, second);
        context.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            CreatedAt = now
        });
        context.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Title",
            NormalizedName = "title",
            EnrichmentStatus = "Completed",
            CreatedAt = now
        });
        context.CatalogReleases.Add(NewCatalogRelease(AllowedReleaseId, 1, 1, now));
        context.SaveChanges();
        context.Users.Add(NewUser(userId, "coherence-user", first.Id, now));
        context.MaterializedLibraryReleases.AddRange(
            NewRelease(first.Id, 1, AllowedReleaseId, 1, isOwned: true, isExposed: true),
            NewRelease(second.Id, 1, AllowedReleaseId, 2, isOwned: false, isExposed: true));

        return (first.Id, second.Id);
    }

    private static RomdUser NewUser(
        Guid id,
        string userName,
        int? libraryId,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.test",
            NormalizedEmail = $"{userName}@example.test".ToUpperInvariant(),
            LibraryId = libraryId,
            CreatedAt = now
        };

    private static MaterializedLibraryReleaseEntity NewRelease(
        int libraryId,
        int titleId,
        int catalogReleaseId,
        int datGameId,
        bool isOwned,
        bool isExposed) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            CatalogReleaseId = catalogReleaseId,
            DatGameId = datGameId,
            DatFileId = 1,
            PlatformId = 1,
            IsEligible = isExposed,
            IsComplete = true,
            IsOwned = isOwned,
            IsPlayable = isOwned && isExposed,
            IsBlocked = !isExposed,
            BlockReason = isExposed ? null : "ExcludedDat",
            IsExposed = isExposed,
            ExposureReason = isExposed ? "ExposedDefault" : "ExcludedDat"
        };

    private static CatalogReleaseEntity NewCatalogRelease(
        int id,
        int titleId,
        int platformId,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            PlatformId = platformId,
            CatalogTitleId = titleId,
            Fingerprint = $"release-{id}",
            Name = $"Release {id}",
            CreatedAt = now,
            UpdatedAt = now
        };

    private sealed class PauseAfterLiveAssignmentResolutionInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilResolvedAsync() => _resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AspNetUsers", StringComparison.Ordinal) &&
                command.CommandText.Contains("NeedsMaterialization", StringComparison.Ordinal))
            {
                _resolved.TrySetResult();
                await _resume.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            return result;
        }
    }
}
