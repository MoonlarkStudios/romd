using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MediaFacts;

/// <summary>Current trusted-import assertions, bounded per complete published tuple and read in the fact snapshot.</summary>
internal sealed class DiscReferenceAssertionReader(RomdDbContext context)
{
    internal Task<IReadOnlyDictionary<int, IReadOnlyList<RomDiscMediaReferenceRecord>>> ReadAsync(IReadOnlyList<RomFileEntity> roms, CancellationToken ct) =>
        ReadCoreAsync(roms, false, ct);

    // A mutation reads the complete bounded assertion set in one statement. A positive PS2 publication
    // still requires the separate current-reference authority and its tuple assertion lock.
    internal Task<IReadOnlyDictionary<int, IReadOnlyList<RomDiscMediaReferenceRecord>>> ReadLockedPublicationAsync(IReadOnlyList<RomFileEntity> roms, CancellationToken ct) =>
        ReadCoreAsync(roms, true, ct);

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<RomDiscMediaReferenceRecord>>> ReadCoreAsync(IReadOnlyList<RomFileEntity> roms, bool lockedPublication, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roms);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roms.Count, 256);
        var isolation = context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
        if (lockedPublication ? isolation != IsolationLevel.ReadCommitted : isolation is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
            throw new InvalidOperationException("Reference assertions require the complete fact snapshot or the publisher's protected ReadCommitted transaction.");
        if (roms.Count == 0)
            return new Dictionary<int, IReadOnlyList<RomDiscMediaReferenceRecord>>();
        // The guarded function reads at most 129 assertions for each retained logical tuple;
        // API credentials never gain table or raw-body access. Its STABLE read shares this snapshot.
        var rows = await context.Database.SqlQueryRaw<AssertionRow>(
            "SELECT * FROM romd.read_disc_reference_assertions(@ids::integer[])",
            new NpgsqlParameter("ids", roms.Select(rom => rom.Id).ToArray())).ToArrayAsync(ct);
        try
        {
            return rows.GroupBy(row => row.RomId).ToDictionary(group => group.Key, group => (IReadOnlyList<RomDiscMediaReferenceRecord>)Array.AsReadOnly(
                group.Select(row => new RomDiscMediaReferenceRecord(row.Id, EnumValue<RomDiscReferenceSystem>(row.System), EnumValue<RomDiscReferenceMedia>(row.Media),
                    row.FileCount, row.LogicalBytes, Sha1.FromSpan(row.Sha1), Md5.FromSpan(row.Md5), Crc32.FromUInt32(checked((uint)row.Crc32)), row.RawReferenceBytes,
                    Sha256.FromSpan(row.RawReferenceSha256), row.ReferenceSource, row.ParserProfile, row.AcceptedSourceProvenance, new(row.RetrievedAtUtcTicks, TimeSpan.Zero))).ToArray()));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new InvalidDataException("Retained disc reference assertions are contradictory.", error); }
    }
    private static T EnumValue<T>(string text) where T : struct, Enum => Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) && value.ToString() == text
        ? value : throw new InvalidDataException("Unknown disc reference assertion.");
    private sealed class AssertionRow
    {
        public int RomId { get; init; }
        public Guid Id { get; init; }
        public string System { get; init; } = ""; public string Media { get; init; } = "";
        public int FileCount { get; init; }
        public long LogicalBytes { get; init; }
        public byte[] Sha1 { get; init; } = []; public byte[] Md5 { get; init; } = [];
        public long Crc32 { get; init; }
        public long RawReferenceBytes { get; init; }
        public byte[] RawReferenceSha256 { get; init; } = [];
        public string ReferenceSource { get; init; } = ""; public string ParserProfile { get; init; } = ""; public string AcceptedSourceProvenance { get; init; } = "";
        public long RetrievedAtUtcTicks { get; init; }
    }
}
