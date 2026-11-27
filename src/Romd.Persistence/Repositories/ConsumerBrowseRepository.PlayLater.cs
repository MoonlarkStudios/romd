using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;

namespace Romd.Persistence.Repositories;

public sealed partial class ConsumerBrowseRepository
{
    public Task<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>> ListPlayLaterTitlesAsync(
        ConsumerLibraryScope scope, ConsumerReleasePreference releasePreference, string? cursor, int limit,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerTitleCardData>>(scope,
            (libraryId, token) => ReadPlayLaterPageAsync(scope.UserId, libraryId, releasePreference, cursor, limit, token), ct);

    public Task<ConsumerLibraryReadResult<IReadOnlyList<int>>> GetPlayLaterMembershipAsync(
        ConsumerLibraryScope scope, int[] titleIds, CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<IReadOnlyList<int>>(scope, async (libraryId, token) =>
        {
            var ids = await VisiblePlayLaterEntries(scope.UserId, libraryId)
                .Where(entry => titleIds.Contains(entry.TitleId))
                .Select(entry => entry.TitleId)
                .Order()
                .ToArrayAsync(token);
            return new ConsumerLibraryProjectionResult<IReadOnlyList<int>>.Found(ids);
        }, ct);

    public async Task<bool> StagePlayLaterMembershipAsync(
        ConsumerLibraryScope scope, int titleId, DateTimeOffset? addedAt, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A Play Later write requires a caller-owned transaction.");

        // All writes to the same list share this lock, including requests that reach the
        // same title through a merge redirect. Read Committed sees the preceding holder's commit.
        string lockName = $"play-later:{scope.UserId}";
        await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", ct);
        await CatalogTopologyFence.AcquireSharedAsync(context, ct);
        var result = await _liveConsumerLibrary.ReadInTransactionAsync<int>(scope,
            (libraryId, token) => ResolveVisiblePlayLaterTitleAsync(scope.UserId, libraryId, titleId, token), ct);
        if (result is not ConsumerLibraryReadResult<int>.Found { Value: var canonicalTitleId })
            return false;

        var entry = await context.PlayLaterEntries.AsTracking()
            .SingleOrDefaultAsync(item => item.UserId == scope.UserId && item.TitleId == canonicalTitleId, ct);
        if (addedAt is { } timestamp && entry is null)
        {
            context.PlayLaterEntries.Add(new PlayLaterEntryEntity
            {
                UserId = scope.UserId,
                TitleId = canonicalTitleId,
                AddedAt = timestamp
            });
        }
        else if (addedAt is null && entry is not null)
        {
            context.PlayLaterEntries.Remove(entry);
        }

        return true;
    }

    private async Task<ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>> ReadPlayLaterPageAsync(
        Guid userId, int libraryId, ConsumerReleasePreference releasePreference, string? cursor, int limit,
        CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("consumer.play-later", userId, libraryId);
        var decoded = KeysetCursor.Decode<PlayLaterPosition>(cursor, fingerprint,
            position => position.TitleId > 0 && position.AddedAt != default);
        if (decoded.IsError)
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Rejected(decoded.FirstError);

        var query = VisiblePlayLaterEntries(userId, libraryId);
        if (decoded.Value is { } after)
            query = query.Where(entry => entry.AddedAt < after.AddedAt ||
                entry.AddedAt == after.AddedAt && entry.TitleId < after.TitleId);

        var positions = await query.OrderByDescending(entry => entry.AddedAt).ThenByDescending(entry => entry.TitleId)
            .Select(entry => new PlayLaterPosition(entry.AddedAt, entry.TitleId))
            .Take(limit + 1)
            .ToListAsync(ct);
        bool hasNextPage = positions.Count > limit;
        if (hasNextPage)
            positions.RemoveAt(limit);

        var titleIds = positions.Select(position => position.TitleId).ToArray();
        var titleRows = await ProjectToTitleCards(context.PresentedTitles().Where(title => titleIds.Contains(title.Id)))
            .ToListAsync(ct);
        var cards = await CompleteTitleCardsAsync(titleRows, libraryId, releasePreference, ct);
        var cardsById = cards.ToDictionary(card => card.Id);
        return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Found(new(
            titleIds.Select(id => cardsById[id]).ToArray(),
            hasNextPage ? KeysetCursor.Encode(positions[^1], fingerprint) : null,
            hasNextPage));
    }

    private IQueryable<PlayLaterEntryEntity> VisiblePlayLaterEntries(Guid userId, int libraryId) =>
        context.PlayLaterEntries.Where(entry => entry.UserId == userId &&
            OwnedMaterializedLibraryTitles(libraryId).Any(title => title.TitleId == entry.TitleId));

    private async Task<ConsumerLibraryProjectionResult<int>> ResolveVisiblePlayLaterTitleAsync(
        Guid userId, int libraryId, int requestedTitleId, CancellationToken ct)
    {
        int? canonicalTitleId = await OwnedMaterializedLibraryTitles(libraryId)
            // An account reassignment does not need the catalog fence. Confirm the assignment in
            // the same statement as the visibility check before admitting a mutation.
            .Where(title => context.Users.Any(user => user.Id == userId && user.LibraryId == title.LibraryId))
            .Where(title => title.TitleId == requestedTitleId || context.TitleMergeRedirects.Any(redirect =>
                redirect.SourceTitleId == requestedTitleId && redirect.TargetTitleId == title.TitleId))
            .Select(title => (int?)title.TitleId)
            .SingleOrDefaultAsync(ct);

        return canonicalTitleId is { } id
            ? new ConsumerLibraryProjectionResult<int>.Found(id)
            : new ConsumerLibraryProjectionResult<int>.ItemNotFound();
    }

    private sealed record PlayLaterPosition(DateTimeOffset AddedAt, int TitleId);
}
