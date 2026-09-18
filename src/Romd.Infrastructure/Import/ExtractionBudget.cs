using ErrorOr;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Import;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Job-wide extraction budget carried across every extraction round of one import, including
///     nested archives. Entries and bytes are charged LIVE as extractors create output: every
///     file and directory (including implicit parents) charges one entry via
///     <see cref="TryChargeEntry" />, and every write chunk charges bytes via
///     <see cref="TryChargeBytes" /> — an archive whose enumeration under-reports its real output
///     is stopped mid-write, not detected after. Each archive's declared totals are still
///     pre-checked against the remaining budget as an early gate, and the byte budget is
///     re-clamped to the volume's current free space before each archive because other work can
///     consume the volume mid-job. <see cref="ArchiveExtractionLimits.MaxTotalEntries" /> is a
///     WORKSPACE ceiling: entries already staged before extraction consume it up front, and
///     nothing is ever credited back (not even a deleted extracted archive), so the budget is
///     monotonic like the byte budget. Extraction is sequential today (one archive, one entry at
///     a time), but the budget is shared job-wide state, so charges are lock-guarded and stay
///     correct if an extractor ever writes in parallel.
/// </summary>
public sealed class ExtractionBudget : IExtractionQuota
{
    private readonly Lock _lock = new();
    private readonly long _spaceFloorBytes;
    private long _remainingEntries;
    private long _remainingBytes;

    /// <param name="limits">Caps and budgets for the whole import job.</param>
    /// <param name="availableBytes">Free space on the workspace volume at job start.</param>
    /// <param name="stagedEntryCount">
    ///     Files and directories already staged in the workspace. They consume the entry ceiling
    ///     up front — a near-limit import must not double the workspace entry count by
    ///     extracting — and are never credited back when extraction deletes a processed archive.
    /// </param>
    public ExtractionBudget(ArchiveExtractionLimits limits, long availableBytes, long stagedEntryCount)
    {
        _spaceFloorBytes = limits.SpaceFloorBytes;
        _remainingEntries = Math.Max(0, limits.MaxTotalEntries - stagedEntryCount);
        _remainingBytes = Math.Max(0, availableBytes - _spaceFloorBytes);
    }

    public long RemainingEntries
    {
        get
        {
            lock (_lock)
            {
                return _remainingEntries;
            }
        }
    }

    public long RemainingBytes
    {
        get
        {
            lock (_lock)
            {
                return _remainingBytes;
            }
        }
    }

    /// <summary>Pre-checks an archive's declared totals (files and directories) against the remaining budget.</summary>
    public ErrorOr<Success> ValidateDeclared(
        string archiveName,
        int declaredEntries,
        long declaredUncompressedBytes)
    {
        lock (_lock)
        {
            if (declaredEntries > _remainingEntries)
            {
                return ImportErrors.ExtractionBudgetExceeded(archiveName, declaredEntries, _remainingEntries);
            }

            return declaredUncompressedBytes <= _remainingBytes
                ? Result.Success
                : ImportErrors.ArchiveExpansionExceedsSpace(archiveName, declaredUncompressedBytes, _remainingBytes);
        }
    }

    /// <summary>
    ///     Re-clamps the byte budget to the volume's current free space minus the reserved floor.
    ///     The budget only ever shrinks: other jobs consuming the volume must not leave this
    ///     import spending space that no longer exists.
    /// </summary>
    public void ClampBytesToAvailableSpace(long availableBytes)
    {
        lock (_lock)
        {
            _remainingBytes = Math.Min(_remainingBytes, Math.Max(0, availableBytes - _spaceFloorBytes));
        }
    }

    public bool TryChargeEntry()
    {
        lock (_lock)
        {
            if (_remainingEntries <= 0)
            {
                return false;
            }

            _remainingEntries--;
            return true;
        }
    }

    public bool TryChargeBytes(long byteCount)
    {
        lock (_lock)
        {
            if (byteCount > _remainingBytes)
            {
                return false;
            }

            _remainingBytes -= byteCount;
            return true;
        }
    }
}
