using Microsoft.EntityFrameworkCore;
using ErrorOr;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Romd.Application.Common.Security;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;

namespace Romd.Media.Probe;

/// <summary>Production stores and snapshot reads for disposable qualification databases.</summary>
internal static class ProbeMediaFacts
{
    internal static RomPreparationService ManagedPreparation(IHashingService hashing, RvzVerifier rvz, IRomDiscMediaReferenceStore references) =>
        new(new RomPreparationAdapterRegistry(hashing, () => rvz, () => new RomOriginalMediaIdentifier(rvz),
            () => throw new NotSupportedException("This managed probe has no native DVD assets."),
            () => throw new NotSupportedException("This managed probe has no native CD assets.")), new RomPs2DvdEligibilityResolver(references));

    internal sealed class EmptyReferences : IRomDiscMediaReferenceStore
    {
        public Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query,
            int maximumResults, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>>(Array.Empty<RomDiscMediaReferenceRecord>());
        }
    }
    internal static ChdDeliveryAdmission ManagedAdmission { get; } = CreateManagedAdmission();
    private static ChdDeliveryAdmission CreateManagedAdmission()
    {
        var media = RomMediaPolicyComposition.Create();
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), requireTool: false);
        return new(runtime, new MediaHostCapabilities(runtime, media, encodes: false), media.Kinds);
    }
    internal static IAuditContext Audit { get; } = new SystemAudit();
    internal static RomMediaObservationStore Observations(RomdDbContext db) => new(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
    internal static RomMediaCreationStore Creations(RomdDbContext db) => new(db);
    internal static async Task<RomContentDescriptor> DescribeAsync(RomdDbContext db, int romId, CancellationToken ct) =>
        await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(romId, ct) ?? throw new InvalidDataException("Qualified logical ROM no longer has a retained complete graph.");
    internal static Task<int> OwnedRepresentationsAsync(RomdDbContext db, int romId, CancellationToken ct) =>
        db.RepresentationOutputs.Where(output => output.RomId == romId).Select(output => output.RepresentationId).Distinct().CountAsync(ct);
    internal static Task<int> VerificationsAsync(RomdDbContext db, int romId, CancellationToken ct) =>
        db.Verifications.CountAsync(proof => db.RepresentationOutputs.Any(output => output.RomId == romId && output.RepresentationId == proof.RepresentationId), ct);
    internal static async Task<int> CreationsAsync(RomdDbContext db, int romId, CancellationToken ct) =>
        (await DescribeAsync(db, romId, ct)).Artifacts.SelectMany(artifact => artifact.Facts.Creations).Select(creation => creation.Id).Distinct().Count();
    private sealed class SystemAudit : IAuditContext
    {
        public Guid ActorId => Guid.Empty;
        public bool IsSystem => true;
    }
}
