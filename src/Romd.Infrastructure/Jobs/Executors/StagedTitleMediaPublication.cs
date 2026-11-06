using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Jobs.Executors;

internal static class StagedTitleMediaPublication
{
    /// <summary>
    ///     Maps an already-read publication batch onto its title. The existing mutation owner acquires
    ///     locks, publishes, flushes required file identities and reads the receipt before calling this.
    /// </summary>
    internal static void AttachPublishedMedia(this Title title, IReadOnlyList<StagedTitleMedia> media,
        IReadOnlyList<FileStoreResult> files, DateTimeOffset createdAt)
    {
        if (files.Count != media.Count)
            throw new ArgumentException("Published files must match the staged media in input order.", nameof(files));
        for (int i = 0; i < media.Count; i++)
            title.AddOrReplaceMedia(media[i].ToTitleMedia(title.Id, files[i].File.Id, createdAt));
    }
}
