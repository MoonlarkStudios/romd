using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Domain.Source.Rom;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Real fresh-model repositories and publication composition using the test context's complete options, including interceptors.</summary>
public static class MediaTestServices
{
    public static DbContextOptions<RomdDbContext> ReadOptions(RomdDbContext db) =>
        (DbContextOptions<RomdDbContext>)db.GetService<IDbContextOptions>();
    public static RomRepository CreateRomRepository(RomdDbContext db) => new(db, new MediaFactPublisher(db), ReadOptions(db));
    public static RomContentCatalog CreateContentCatalog(RomdDbContext db) => new(db, ReadOptions(db));
    public static Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests CreateReconciliationRequests(RomdDbContext db, TimeProvider clock) =>
        new(db, clock);
    public static RomMediaObservationStore CreateObservationStore(RomdDbContext db, TimeProvider clock) =>
        new(db, CreateReconciliationRequests(db, clock));
    public static RomMediaCreationStore CreateCreationStore(RomdDbContext db) => new(db);
    public static RomPs2CurrentReferenceAuthority CreatePs2Authority(RomdDbContext db) =>
        new(db, new RomPs2DvdEligibilityResolver(new RomDiscMediaReferenceStore(db)));
    public static RomRepresentationPublisher CreatePublisher(RomdDbContext db, IFileStorageService files, IUnitOfWork work,
        RomMediaPolicyRegistry policies, RomEncodingRegistry encodings, IFileMutationLock fileLocks, IJobItemRepository? outcomes,
        IAuditContext audit, TimeProvider clock) => new(files, work, CreateRomRepository(db), policies, encodings, fileLocks, outcomes,
            new MediaFactPublisher(db), CreateObservationStore(db, clock), CreateCreationStore(db), audit, clock, CreateReconciliationRequests(db, clock), CreatePs2Authority(db));
}
