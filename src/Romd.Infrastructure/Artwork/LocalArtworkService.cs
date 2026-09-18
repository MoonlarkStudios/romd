using System.Security.Cryptography;
using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Storage;

namespace Romd.Infrastructure.Artwork;

/// <summary>Snapshot existing media into immutable, validated role-specific artwork without changing its type.</summary>
public sealed class LocalArtworkService(IArtworkCurationRepository repository, IArtworkImageProcessor processor,
    IFileStorageService files, IContentAddressableStore store, IFileMutationLock fileLocks,
    IUnitOfWork unitOfWork) : ILocalArtworkService
{
    public async Task<ErrorOr<long>> PinMediaAsync(int titleId, ArtworkRole role, int mediaId, long expectedRevision,
        Guid actorId, CancellationToken ct = default, int focalX = 50, int focalY = 50)
    {
        if (focalX is < 0 or > 100 || focalY is < 0 or > 100) return ArtworkCurationErrors.InvalidRequest();
        if (!Enum.IsDefined(role) || expectedRevision < 0) return ArtworkCurationErrors.InvalidRequest();
        var media = await repository.FindMediaAsync(titleId, mediaId, ct);
        if (media is null) return ArtworkCurationErrors.InvalidRequest();
        await using var stream = await files.RetrieveByIdAsync(media.FileId, ct);
        if (stream is null) return ArtworkCurationErrors.InvalidRequest();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 20 * 1024 * 1024) return ArtworkImageErrors.TooLarge();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        var bytes = buffer.ToArray();
        var processed = await processor.ProcessAsync(bytes, role, ct);
        if (processed.IsError) return processed.Errors;
        var image = processed.Value;

        // Decode outside the transaction; compare the selection revision again before publishing.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var locked = await repository.LockSelectionAsync(titleId, role, expectedRevision, ct);
        if (locked.IsError) return locked.Errors;
        var currentMedia = await repository.FindMediaAsync(titleId, mediaId, ct);
        if (currentMedia?.FileId != media.FileId) return ArtworkCurationErrors.SelectionChanged();
        foreach (var hash in image.Variants.Select(variant => Hash(variant.EncodedBytes)).Append(Hash(bytes))
                     .Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal))
            await fileLocks.AcquireAsync(hash, ct);
        var original = await StoreAsync(bytes, image.ContentType, image.Width, image.Height, "original", ct);
        var variants = new List<RetainedArtworkFile>();
        foreach (var variant in image.Variants)
            variants.Add(await StoreAsync(variant.EncodedBytes, variant.ContentType, variant.Width, variant.Height, variant.Name, ct));
        await repository.StageLocalAssetAsync(titleId, role, media.SourceId,
            new RetainedArtworkContent(original, variants, null, null), actorId, ct);
        await unitOfWork.FlushAsync(ct);
        var gallery = await repository.GetGalleryAsync(titleId, ct);
        if (gallery.IsError) return gallery.Errors;
        var asset = gallery.Value.Single(item => item.Role == role && item.SourceId == media.SourceId &&
            item.ProviderAssetId is null && item.Original.ContentVersion == original.Hash.ToString());
        var result = await repository.StagePinAsync(titleId, role, asset.Id, expectedRevision, ct, focalX, focalY);
        if (result.IsError) return result.Errors;
        await transaction.CommitAsync(ct);
        return result.Value;
    }

    private async Task<RetainedArtworkFile> StoreAsync(byte[] bytes, string contentType, int width, int height,
        string name, CancellationToken ct)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        var result = await store.StoreAsync(stream, null, ct);
        return new RetainedArtworkFile(result.Key.Hash, result.Size, result.CompressedSize, result.IsCompressed,
            contentType, width, height, name);
    }

    private static Sha256 Hash(byte[] bytes) => Sha256.FromSpan(SHA256.HashData(bytes));
}
