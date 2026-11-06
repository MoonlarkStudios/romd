using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Loads raw logical observations within the caller's complete fact snapshot.</summary>
internal sealed class LogicalInspectionReader(RomdDbContext context)
{
    internal Task<IReadOnlyDictionary<int, IReadOnlyList<RomLogicalInspection>>> ReadAsync(int[] romIds, CancellationToken ct) =>
        ReadCoreAsync(romIds, false, ct);
    internal Task<IReadOnlyDictionary<int, IReadOnlyList<RomLogicalInspection>>> ReadLockedPublicationAsync(int[] romIds, CancellationToken ct) =>
        ReadCoreAsync(romIds, true, ct);

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<RomLogicalInspection>>> ReadCoreAsync(int[] romIds, bool locked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(romIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(romIds.Length, 256);
        foreach (int id in romIds)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var isolation = context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
        if (locked ? isolation != IsolationLevel.ReadCommitted : isolation is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
            throw new InvalidOperationException("Logical observations require a fact snapshot or locked publication transaction.");
        if (romIds.Length == 0)
            return new Dictionary<int, IReadOnlyList<RomLogicalInspection>>();
        var rows = await context.Database.SqlQueryRaw<InspectionRow>("""
            SELECT i.*,r."ContentSha256",r."ContentSize",r."Sha1",r."Md5",r."Crc32"
            FROM romd."LogicalInspections" i JOIN romd."Roms" r ON r."Id" = i."RomId"
            WHERE i."RomId" = ANY (@ids) ORDER BY i."ObservedAtUtcTicks",i."Id"
            """, new NpgsqlParameter("ids", romIds)).ToArrayAsync(ct);
        var boots = (await context.Database.SqlQueryRaw<BootRow>("""
            SELECT b.* FROM romd."Ps2BootObservations" b JOIN romd."LogicalInspections" i ON i."Id" = b."InspectionId"
            WHERE i."RomId" = ANY (@ids)
            """, new NpgsqlParameter("ids", romIds)).ToArrayAsync(ct)).ToDictionary(row => row.InspectionId);
        try
        {
            return rows.GroupBy(row => row.RomId).ToDictionary(group => group.Key, group => (IReadOnlyList<RomLogicalInspection>)
                Array.AsReadOnly(group.Select(row =>
                {
                    boots.TryGetValue(row.Id, out var boot);
                    if (row.HasPs2Boot != (boot is not null))
                        throw new InvalidDataException("Logical boot observation is incomplete.");
                    RomNintendoLayoutFacts? nintendo = row.NintendoPartitions is { } partitions
                        ? new(partitions, row.StandardKeyPartitions ?? -1, row.KoreanKeyPartitions ?? -1) : null;
                    if (nintendo is null && (row.StandardKeyPartitions is not null || row.KoreanKeyPartitions is not null))
                        throw new InvalidDataException("Logical partition observation is incomplete.");
                    RomPspLayoutFacts? psp = row.PspVolumeBlocks is { } blocks
                        ? new(row.ContentSize, checked((uint)blocks), row.PspDiscId ?? "", row.PspDiscVersion ?? "") : null;
                    if (psp is null && (row.PspDiscId is not null || row.PspDiscVersion is not null))
                        throw new InvalidDataException("Logical PSP observation is incomplete.");
                    var layout = RomLayoutObservation.Exclusive(nintendo, boot?.ToDomain(), psp);
                    return new RomLogicalInspection(row.Id, new(row.ContentSize, Sha256.FromSpan(row.ContentSha256), Sha1.FromSpan(row.Sha1),
                        Md5.FromSpan(row.Md5), Crc32.FromSpan(row.Crc32)), row.Profile, row.InspectorBuild,
                        new(row.ObservedAtUtcTicks, TimeSpan.Zero), row.Prefix, layout);
                }).ToArray()));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new InvalidDataException("Retained logical observations are contradictory.", error); }
    }

    private sealed class InspectionRow
    {
        public Guid Id { get; init; }
        public int RomId { get; init; }
        public string Profile { get; init; } = ""; public string InspectorBuild { get; init; } = "";
        public long ObservedAtUtcTicks { get; init; }
        public byte[] Prefix { get; init; } = [];
        public byte[] ContentSha256 { get; init; } = []; public long ContentSize { get; init; }
        public byte[] Sha1 { get; init; } = []; public byte[] Md5 { get; init; } = []; public byte[] Crc32 { get; init; } = [];
        public int? NintendoPartitions { get; init; }
        public int? StandardKeyPartitions { get; init; }
        public int? KoreanKeyPartitions { get; init; }
        public bool HasPs2Boot { get; init; }
        public long? PspVolumeBlocks { get; init; }
        public string? PspDiscId { get; init; }
        public string? PspDiscVersion { get; init; }
    }
    private sealed class BootRow
    {
        public Guid InspectionId { get; init; }
        public long LogicalBytes { get; init; }
        public long VolumeBlocks { get; init; }
        public long PrimaryDescriptorBlock { get; init; }
        public long RootDirectoryBlock { get; init; }
        public long RootDirectoryBytes { get; init; }
        public long SystemCnfBlock { get; init; }
        public long SystemCnfBytes { get; init; }
        public long BootExecutableBlock { get; init; }
        public long BootExecutableBytes { get; init; }
        public string ObservedBoot2Value { get; init; } = ""; public string BootPath { get; init; } = ""; public string BootSerial { get; init; } = "";
        public long EntryPoint { get; init; }
        public int ProgramHeaderCount { get; init; }
        public string Profile { get; init; } = ""; public string InspectorBuild { get; init; } = "";
        public RomPs2BootLayoutFacts ToDomain() => new(LogicalBytes, checked((uint)VolumeBlocks), checked((uint)PrimaryDescriptorBlock),
            checked((uint)RootDirectoryBlock), checked((uint)RootDirectoryBytes), checked((uint)SystemCnfBlock), checked((uint)SystemCnfBytes),
            checked((uint)BootExecutableBlock), checked((uint)BootExecutableBytes), ObservedBoot2Value, BootPath, BootSerial,
            checked((uint)EntryPoint), checked((ushort)ProgramHeaderCount), Profile, InspectorBuild);
    }
}
