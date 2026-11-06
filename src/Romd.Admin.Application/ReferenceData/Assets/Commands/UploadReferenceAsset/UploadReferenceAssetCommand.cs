using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.ReferenceData.Assets.Commands.UploadReferenceAsset;

public sealed record UploadReferenceAssetCommand(byte[] Bytes, string ContentType) : ICommand<string>;

public sealed class UploadReferenceAssetCommandHandler(IReferenceAssetReservationStore reservations,
    IFileStorageService files, IUnitOfWork work, TimeProvider clock, ILogger<UploadReferenceAssetCommandHandler> logger)
    : ICommandHandler<UploadReferenceAssetCommand, string>
{
    public async Task<ErrorOr<string>> HandleAsync(UploadReferenceAssetCommand command, CancellationToken ct = default)
    {
        if (command.Bytes.Length is < 12 or > 2097152)
            return Invalid("Icons must be at most 2 MiB.");

        // Own the bytes validated and measured before entering either database lock boundary.
        byte[] bytes = command.Bytes.ToArray();
        if (!IsRaster(bytes, command.ContentType))
            return Invalid("Use a PNG or WebP icon.");

        using var content = new MemoryStream(bytes, writable: false);
        var staged = await files.StageAsync(content, ct);
        IStagedFile? owned = staged;
        bool failed = false;
        try
        {
            return await work.ExecuteInTransactionAsync<string>(async token =>
            {
                await reservations.AcquireAsync(token);
                var publication = await files.PublishStagedAsync(staged, token);
                if (publication.RequiresFlush)
                    await work.FlushAsync(token);
                var published = publication.ReadAfterFlush();
                var hash = published.File.Sha256;

                // Preserve precommit cleanup: a staging failure must roll back acceptance. Clear
                // ownership before disposal so a failed attempt is not repeated by the finally.
                owned = null;
                await staged.DisposeAsync();
                await reservations.ReserveStagedAsync(hash, published.File.Id, command.ContentType,
                    clock.GetUtcNow().AddDays(7), token);
                return hash.ToString();
            }, logger, ct);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (owned is { } cleanup)
            {
                owned = null;
                try
                {
                    await cleanup.DisposeAsync();
                }
                catch (Exception error) when (failed)
                {
                    logger.LogWarning(error, "Reference asset staging cleanup failed after acceptance failed");
                }
            }
        }
    }

    // Uploaded icons are raster images. Reviewed built-in assets have a separate SVG policy.
    private static bool IsRaster(byte[] bytes, string contentType) =>
        contentType == "image/png" && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        || contentType == "image/webp" && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
        && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);

    private static Error Invalid(string message) => Error.Validation("ReferenceCatalog.Invalid", message);
}
