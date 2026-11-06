using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Export;

public sealed record ExportArchiveMember(ExportArchiveEntry Entry, IReadOnlyList<RomDeliveryOption> Formats);
public sealed record ExportArchivePlan(IReadOnlyList<ExportArchiveEntry> Entries,
    IReadOnlyList<ExportArchiveEntry> MissingEntries);

public static class ExportArchivePlanner
{
    public const int MaximumTicketEntries = 10_000;

    public static ErrorOr<ExportArchivePlan> Plan(IReadOnlyList<ExportArchiveMember> members,
        RomDeliveryFormat format, bool skipMissing = false)
    {
        if (format is not (RomDeliveryFormat.Original or RomDeliveryFormat.Rvz or RomDeliveryFormat.Chd))
            return RomDeliveryErrors.InvalidFormat();
        var entries = new List<ExportArchiveEntry>();
        var missing = new List<ExportArchiveEntry>();
        foreach (var member in members)
        {
            if (!ExportArchivePaths.TryNormalize(member.Entry.Path, out var path))
                return ExportErrors.InvalidPaths();
            if (member.Entry.RomId is not > 0 || member.Entry.FileId <= 0)
                return ExportErrors.InvalidPaths();
            var selected = format != RomDeliveryFormat.Original
                ? member.Formats.FirstOrDefault(option => option.Format == format
                    && option.State == RomDeliveryState.Available && option.Target is not null)
                : null;
            selected ??= member.Formats.FirstOrDefault(option => option.Format == RomDeliveryFormat.Original
                && option.State == RomDeliveryState.Available && option.Target is not null);
            if (selected?.Target is not { } target)
            {
                if (!skipMissing)
                    return ExportErrors.FileUnavailable();
                missing.Add(member.Entry);
                continue;
            }
            if (target.RomId != member.Entry.RomId || !RomDeliveryTargets.IsValid(target))
                return ExportErrors.FileUnavailable();
            var parent = path[..(path.LastIndexOf('/') + 1)];
            entries.Add(member.Entry with { FileId = target.FileId, Path = parent + target.Filename, Target = target });
        }
        if (!ExportArchivePaths.TryValidate(entries, out _))
            return ExportErrors.InvalidPaths();
        return new ExportArchivePlan(entries.AsReadOnly(), missing.AsReadOnly());
    }

    /// <summary>
    ///     The formats an archive of these members can be requested in: original, then each encoded format whose archive
    ///     binds at least one member in it. Each format is planned exactly as its archive is; a request for another format
    ///     falls back to each member's original.
    /// </summary>
    public static IReadOnlyList<RomDeliveryFormat> Formats(IReadOnlyList<ExportArchiveMember> members) =>
    [
        RomDeliveryFormat.Original, .. Enum.GetValues<RomDeliveryFormat>().Where(format => format != RomDeliveryFormat.Original
            && Plan(members, format) is { IsError: false } plan && plan.Value.Entries.Any(entry => entry.Target?.Format == format))
    ];

    /// <summary>
    ///     <see cref="Formats" /> of each archive, planned from its entries as <see cref="BindAsync" /> plans them. Member
    ///     content is read once, in batches, across all the archives. An archive whose entries cannot be planned offers only
    ///     original, as its archive fails in every format.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyList<RomDeliveryFormat>>> FormatsAsync(
        IReadOnlyList<IReadOnlyList<ExportArchiveEntry>> archives, IRomContentCatalog catalog, RomDeliveryPolicy deliveryPolicy,
        IRomChdDeliveryAdmission? chdAdmission = null, CancellationToken ct = default)
    {
        var contents = new Dictionary<int, RomContentDescriptor?>();
        var romIds = archives.SelectMany(entries => entries).Select(entry => entry.RomId).OfType<int>().Where(id => id > 0).Distinct();
        foreach (var batch in romIds.Chunk(IRomContentCatalog.MaximumBatchSize))
            foreach (var (romId, content) in await catalog.FindByRomIdsAsync(batch, ct))
                contents[romId] = content;
        return archives.Select(entries => Members(entries, contents, deliveryPolicy, chdAdmission, ct) is { IsError: false } members
            ? Formats(members.Value) : [RomDeliveryFormat.Original]).ToArray();
    }

    /// <summary>
    ///     The formats an export of these systems can be requested in: original, then each system's stored delivery format.
    ///     It lists what members may hold, from policy alone; each member binds the requested format where it holds it.
    /// </summary>
    public static IReadOnlyList<RomDeliveryFormat> StoredFormats(IEnumerable<string> systemKeys, RomDeliveryPolicy deliveryPolicy)
    {
        var stored = systemKeys.Select(deliveryPolicy.For).OfType<RomStoredDelivery>().Select(delivery => delivery.Format).ToHashSet();
        return [RomDeliveryFormat.Original, .. Enum.GetValues<RomDeliveryFormat>().Where(format => format != RomDeliveryFormat.Original
            && stored.Contains(format))];
    }

    public static async Task<ErrorOr<ExportArchivePlan>> BindAsync(IReadOnlyList<ExportArchiveEntry> entries,
        RomDeliveryFormat format, IRomContentCatalog catalog, RomDeliveryPolicy deliveryPolicy, bool skipMissing = false,
        CancellationToken ct = default, IRomChdDeliveryAdmission? chdAdmission = null)
    {
        if (format is not (RomDeliveryFormat.Original or RomDeliveryFormat.Rvz or RomDeliveryFormat.Chd))
            return RomDeliveryErrors.InvalidFormat();
        ct.ThrowIfCancellationRequested();
        var romIds = new HashSet<int>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.RomId is not > 0 || entry.FileId <= 0 || !ExportArchivePaths.TryNormalize(entry.Path, out _))
                return ExportErrors.InvalidPaths();
            romIds.Add(entry.RomId.Value);
        }

        // Validate every member before I/O and read each distinct ROM once across archive parents.
        var facts = new Dictionary<int, RomContentDescriptor?>(romIds.Count);
        foreach (var batch in romIds.Chunk(IRomContentCatalog.MaximumBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var found = await catalog.FindByRomIdsAsync(batch, ct);
            foreach (int romId in batch)
                if (found.TryGetValue(romId, out var content))
                    facts.Add(romId, content);
        }
        ct.ThrowIfCancellationRequested();
        var members = Members(entries, facts, deliveryPolicy, chdAdmission, ct);
        return members.IsError ? members.Errors : Plan(members.Value, format, skipMissing);
    }

    /// <summary>Each entry with the delivery options its content offers under its platforms; missing content offers none.</summary>
    private static ErrorOr<IReadOnlyList<ExportArchiveMember>> Members(IReadOnlyList<ExportArchiveEntry> entries,
        IReadOnlyDictionary<int, RomContentDescriptor?> contents, RomDeliveryPolicy deliveryPolicy,
        IRomChdDeliveryAdmission? chdAdmission, CancellationToken ct = default)
    {
        var members = new List<ExportArchiveMember>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.RomId is not > 0 || entry.FileId <= 0 || !ExportArchivePaths.TryNormalize(entry.Path, out var path))
                return ExportErrors.InvalidPaths();
            IReadOnlyList<RomDeliveryOption> formats = contents.GetValueOrDefault(entry.RomId.Value) is { } content
                ? RomDeliveryPlanner.Plan(content, path[(path.LastIndexOf('/') + 1)..], deliveryPolicy,
                    MediaKindResolver.Resolve(deliveryPolicy.Kinds, content, entry.PlatformKeys ?? []), chdAdmission)
                : [];
            members.Add(new(entry, formats));
        }
        return members;
    }
}
