using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Repositories;

namespace Romd.Persistence.MediaFacts;

/// <summary>Stages immutable physical observations against complete retained proof, without caching policy decisions.</summary>
internal sealed class LogicalInspectionFactWriter(RomdDbContext context)
{
    internal async Task<ErrorOr<LogicalInspectionPublication>> StageAsync(int romId, RomRetainedMeasurement retained, RomLogicalInspection observation, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(romId);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(observation);
        ct.ThrowIfCancellationRequested();
        // The schema stores each layout family in its own columns. An unrecognized family is refused before any access,
        // never written as an inspection without its layout.
        (RomNintendoLayoutFacts? Nintendo, RomPs2BootLayoutFacts? Boot, RomPspLayoutFacts? Psp) columns = observation.Layout switch
        {
            null => (null, null, null),
            RomNintendoLayoutFacts layout => (layout, null, null),
            RomPs2BootLayoutFacts layout => (null, layout, null),
            RomPspLayoutFacts layout => (null, null, layout),
            var layout => throw new ArgumentException($"Layout family '{layout.GetType().FullName}' has no persisted form.", nameof(observation))
        };
        var (nintendo, boot, psp) = columns;
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Logical observation publication requires the caller's ReadCommitted transaction.");
        await context.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM romd."Roms" WHERE "Id" = {romId} FOR NO KEY UPDATE""", ct);
        var logical = await context.RomFiles.SingleOrDefaultAsync(row => row.Id == romId, ct);
        if (logical is null || new RomLogicalIdentity(logical.ContentSize, logical.ContentSha256, logical.Sha1, logical.Md5, logical.Crc32) != observation.Identity)
            return RomEligibilityErrors.ConflictingObservation();
        bool source = await (from output in context.RepresentationOutputs
                             join representation in context.RomRepresentations on output.RepresentationId equals representation.Id
                             where output.Id == retained.OutputId && output.RomId == romId && representation.Id == retained.RepresentationId &&
                                 representation.FileId == retained.FileId && context.Verifications.Any(proof => proof.Id == retained.VerificationId && proof.RepresentationId == representation.Id)
                             select output.Id).AnyAsync(ct);
        if (!source)
            return RomEligibilityErrors.MeasurementNotRetained();
        var existing = (await new LogicalInspectionReader(context).ReadLockedPublicationAsync([romId], ct)).GetValueOrDefault(romId) ?? [];
        var same = existing.SingleOrDefault(row => row.Profile == observation.Profile && row.InspectorBuild == observation.InspectorBuild);
        if (same is not null)
            return same.SameObservation(observation) ? new LogicalInspectionPublication(same.Id, false) : RomEligibilityErrors.ConflictingObservation();
        if (boot is not null)
            await RomPs2ReferenceAssertionLock.AcquireAsync(context, new(observation.Identity.Size, observation.Identity.Sha1, observation.Identity.Md5, observation.Identity.Crc32), ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."LogicalInspections" ("Id","RomId","SourceOutputId","SourceVerificationId","Profile","InspectorBuild",
                "ObservedAtUtcTicks","Prefix","NintendoPartitions","StandardKeyPartitions","KoreanKeyPartitions","HasPs2Boot",
                "PspVolumeBlocks","PspDiscId","PspDiscVersion")
            VALUES ({observation.Id},{romId},{retained.OutputId},{retained.VerificationId},{observation.Profile},{observation.InspectorBuild},
                {observation.ObservedAt.UtcTicks},{observation.Prefix.ToArray()},CAST({nintendo?.Partitions} AS integer),
                CAST({nintendo?.StandardKeyPartitions} AS integer),CAST({nintendo?.KoreanKeyPartitions} AS integer),{boot is not null},
                CAST({(long?)psp?.VolumeBlocks} AS bigint),{psp?.DiscId},{psp?.DiscVersion})
            """, ct);
        if (boot is not null)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."Ps2BootObservations" ("InspectionId","LogicalBytes","VolumeBlocks","PrimaryDescriptorBlock","RootDirectoryBlock","RootDirectoryBytes",
                    "SystemCnfBlock","SystemCnfBytes","BootExecutableBlock","BootExecutableBytes","ObservedBoot2Value","BootPath","BootSerial","EntryPoint",
                    "ProgramHeaderCount","Profile","InspectorBuild")
                VALUES ({observation.Id},{boot.LogicalBytes},{(long)boot.VolumeBlocks},{(long)boot.PrimaryDescriptorBlock},{(long)boot.RootDirectoryBlock},{(long)boot.RootDirectoryBytes},
                    {(long)boot.SystemCnfBlock},{(long)boot.SystemCnfBytes},{(long)boot.BootExecutableBlock},{(long)boot.BootExecutableBytes},{boot.ObservedBoot2Value},
                    {boot.BootPath},{boot.BootSerial},{(long)boot.EntryPoint},{(int)boot.ProgramHeaderCount},{boot.Profile},{boot.InspectorBuild})
                """, ct);
        return new LogicalInspectionPublication(observation.Id, true);
    }
}

internal sealed record LogicalInspectionPublication(Guid Id, bool Created);
