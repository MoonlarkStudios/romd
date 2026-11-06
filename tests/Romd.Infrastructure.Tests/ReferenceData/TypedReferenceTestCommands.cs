using Romd.Admin.Application.ReferenceData.Systems.Commands;
using Romd.Admin.Application.ReferenceData.Companies.Commands;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;

namespace Romd.Infrastructure.Tests.ReferenceData;

// Exercise the real application commands and transaction boundary with PostgreSQL.
internal sealed class TypedReferenceTestCommands(RomdDbContext db)
{
    public SystemReferenceReader Systems { get; } = new(db);
    public CompanyReferenceReader Companies { get; } = new(db);

    public Task<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>?> System(string key, CancellationToken ct = default) => Systems.GetAsync(key, ct);
    public Task<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>?> Company(string key, CancellationToken ct = default) => Companies.GetAsync(key, ct);
    public Task<ErrorOr.ErrorOr<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> CreateSystem(CreateSystemDto value, CancellationToken ct = default) =>
        new CreateSystemCommandHandler(new ReferenceMutationSession(db), new SystemReferenceRepository(db, clock: TimeProvider.System), Systems, new CompanyReferenceRepository(db))
            .HandleAsync(new(value.Key, new(value.Name, value.CompactLabel, value.Description, value.Icon, value.Monochrome), value.ManufacturerKeys), ct);
    public Task<ErrorOr.ErrorOr<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> UpdateSystem(string key, SystemPatchDto value, string revision, CancellationToken ct = default) =>
        new UpdateSystemCommandHandler(new ReferenceMutationSession(db), new SystemReferenceRepository(db, clock: TimeProvider.System), Systems, new CompanyReferenceRepository(db))
            .HandleAsync(new(key, new(
                value.Name,
                value.CompactLabel,
                value.Description,
                value.Icon,
                value.Monochrome, value.Changes.ContainsKey("description"), value.Changes.ContainsKey("icon")), RevisionPrecondition.For(revision), value.ManufacturerKeys), ct);
    public Task<ErrorOr.ErrorOr<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> ResetSystem(string key, SystemOverrideField? field, string revision, CancellationToken ct = default) =>
        new ResetSystemOverridesCommandHandler(new ReferenceMutationSession(db), new SystemReferenceRepository(db, clock: TimeProvider.System), Systems).HandleAsync(new(key, field, RevisionPrecondition.For(revision)), ct);
    public Task<ErrorOr.ErrorOr<ErrorOr.Deleted>> DeleteSystem(string key, string revision, CancellationToken ct = default) =>
        new DeleteSystemCommandHandler(new ReferenceMutationSession(db), new SystemReferenceRepository(db, clock: TimeProvider.System), Systems).HandleAsync(new(key, RevisionPrecondition.For(revision)), ct);
    public Task<ErrorOr.ErrorOr<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>> CreateCompany(CreateCompanyDto value, CancellationToken ct = default) =>
        new CreateCompanyCommandHandler(new ReferenceMutationSession(db), new CompanyReferenceRepository(db), Companies).HandleAsync(new(value.Key, new(value.Name, value.Description)), ct);
    public Task<ErrorOr.ErrorOr<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>> UpdateCompany(string key, CompanyOverrides changes, string revision, CancellationToken ct = default) =>
        new UpdateCompanyCommandHandler(new ReferenceMutationSession(db), new CompanyReferenceRepository(db), Companies).HandleAsync(new(key, changes, RevisionPrecondition.For(revision)), ct);
    public Task<ErrorOr.ErrorOr<Romd.Application.Common.ReferenceCatalog.ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>>> ResetCompany(string key, CompanyOverrideField? field, string revision, CancellationToken ct = default) =>
        new ResetCompanyOverridesCommandHandler(new ReferenceMutationSession(db), new CompanyReferenceRepository(db), Companies).HandleAsync(new(key, field, RevisionPrecondition.For(revision)), ct);
    public Task<ErrorOr.ErrorOr<ErrorOr.Deleted>> DeleteCompany(string key, string revision, CancellationToken ct = default) =>
        new DeleteCompanyCommandHandler(new ReferenceMutationSession(db), new CompanyReferenceRepository(db), Companies).HandleAsync(new(key, RevisionPrecondition.For(revision)), ct);
}
