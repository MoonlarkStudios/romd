namespace Romd.Admin.Application.Storage.Files;

/// <summary>Releases bounded batches of interpretations after every logical, archive and active-job owner is gone; leaves physical file collection to the ledger.</summary>
public interface IMediaRepresentationCleanup
{
    Task<int> CollectAsync(int maximum, CancellationToken ct = default);
}
