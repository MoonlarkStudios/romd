using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Search;

namespace Romd.Admin.Application.Titles.Commands.ReplaceTitleLookupNames;

public sealed record ReplaceTitleLookupNamesCommand(int TitleId, IReadOnlyList<string> Names, RevisionPrecondition IfMatch)
    : ICommand<TitleLookupNameSnapshot>;

public sealed class ReplaceTitleLookupNamesCommandHandler(ITitleLookupNameStore store, ITitleRowLock titleLock, IUnitOfWork unitOfWork,
    ILogger<ReplaceTitleLookupNamesCommandHandler> logger) : ICommandHandler<ReplaceTitleLookupNamesCommand, TitleLookupNameSnapshot>
{
    private const int MaxNames = 32;
    private const int MaxNameLength = 200;

    public async Task<ErrorOr<TitleLookupNameSnapshot>> HandleAsync(ReplaceTitleLookupNamesCommand command, CancellationToken ct = default)
    {
        if (command.Names is null || command.Names.Count > MaxNames)
            return CatalogErrors.InvalidLookupNames($"Supply at most {MaxNames} lookup names.");

        var names = new List<TitleLookupName>(command.Names.Count);
        var documents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in command.Names)
        {
            var name = value?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
                return CatalogErrors.InvalidLookupNames($"Each lookup name must contain 1 to {MaxNameLength} characters and no control characters.");
            var document = SearchTextNormalizer.Normalize(name);
            if (document.Length == 0 || document.Length > MaxNameLength)
                return CatalogErrors.InvalidLookupNames("Each lookup name must contain searchable letters or digits.");
            if (!documents.Add(document))
                return CatalogErrors.InvalidLookupNames("Each lookup name must be distinct after search normalization.");
            names.Add(new(name, document));
        }

        return await unitOfWork.ExecuteInTransactionAsync<TitleLookupNameSnapshot>(async token =>
        {
            var revision = await titleLock.LockRevisionAsync(command.TitleId, token);
            if (revision is null)
                return CatalogErrors.TitleNotFound();
            if (!command.IfMatch.IsSatisfiedBy(revision.Value))
                return CatalogErrors.TitlePreconditionFailed();
            return await store.StageReplacementAsync(command.TitleId, revision.Value, names, token);
        }, logger, ct);
    }
}
