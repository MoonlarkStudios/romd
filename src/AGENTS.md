# AGENTS.md - ROMD Backend

.NET backend, contracts, storage, and backend tests.

## Project Map

- `Romd.Domain`: domain entities, value objects, and domain logic. No external
  dependencies.
- `Romd.Application.Common`: shared CQRS, IDs, pagination, configuration, jobs,
  and security ports.
- `Romd.Admin.Application`: admin and curator use cases, read models, and ports.
- `Romd.Consumer.Application`: consumer browse, account, auth, collection,
  library, and delivery use cases.
- `Romd.Contracts.Common`, `Romd.Contracts.Management`,
  `Romd.Contracts.Consumer`: API DTOs and contracts.
- `Romd.Persistence`: EF Core `RomdDbContext`, entities, configurations,
  converters, interceptors, repositories, queries, seeders, and migrations.
  No ASP.NET Core or Hangfire dependency.
- `Romd.Infrastructure`: external tools/providers, Hangfire, identity,
  delivery, readiness, and realtime implementations over `Romd.Persistence`.
- `Romd.Storage`: standalone content-addressable storage layer.
- `Romd.Hosting`: shared host composition, Minimal API endpoints, authorization,
  SignalR, and realtime plumbing.
- `Romd.Admin.Host`, `Romd.Consumer.Host`, `Romd.Worker.Host`: split production
  hosts.
- `tests/`: backend unit, storage, infrastructure, and integration tests.

Read `docs/split-host-topology.md` before changing host ownership, admin versus
consumer boundaries, worker registration, public origins, or deployment
composition.

## Validation

Run from the repo root:

```bash
mise run build
mise run test
mise run test:integration
mise run test:all
```

Use `mise run test` as the default backend safety check. Run
`mise run test:integration` when a change touches host topology, deployment
composition, Hangfire worker registration, integration fixtures, or
admin/consumer host behavior.

If validation fails unexpectedly, read `docs/known-issues.md` before retrying or
changing behavior.

## C# Conventions

- `sealed` classes by default for implementations.
- `ErrorOr<T>` for handler return types; never throw for business logic.
- Errors live as static factories in `*Errors` classes.
- Primary constructors on records; traditional constructors on service classes.
- Use value objects (`Sha1`, `Md5`, `Crc32`, `Sqid`, `StorageKey`) over raw
  primitives.
- File-scoped namespaces, expression-bodied members, pattern matching, LINQ
  pipelines where they stay clear.
- `RomdDbContext` uses global NoTracking. Any repository method that mutates a
  loaded entity must query with `.AsTracking()`.
- `.editorconfig` owns formatting. Do not add separate formatters.
- Optimize hot ROM/hash paths with streaming, `Span<T>`, and pooled buffers
  where appropriate.

## Architecture

- Clean Architecture layers: Domain -> Application -> Persistence and
  Infrastructure -> Host. Domain has zero external dependencies. Application
  defines interfaces; Persistence (EF Core) and Infrastructure implement them.
- CQRS: one class per file under
  `src/Romd.Admin.Application/{Area}/{Commands|Queries}/{UseCaseName}/` or
  `src/Romd.Consumer.Application/{Area}/{Commands|Queries}/{UseCaseName}/`.
- Handlers implement `ICommandHandler<TCmd, TResult>` or
  `IQueryHandler<TQuery, TResult>` with
  `async Task<ErrorOr<T>> HandleAsync(T input, CancellationToken ct = default)`.
- Minimal API endpoints live in `src/Romd.Hosting/Endpoints/` as static
  `MapXxxEndpoints` extension classes.
- Use keyset pagination with cursors, never offset pagination.
- Repositories return domain models, not EF entities.
- If a backend endpoint or OpenAPI contract change affects web API clients, use
  `.agents/skills/romd-web-api-client/SKILL.md`.

## Testing

- xUnit + Shouldly assertions + NSubstitute mocks.
- Test names: `MethodName_Scenario_ExpectedBehavior`.
- Use Arrange / Act / Assert structure.
- Integration tests use `IntegrationTestCollection` shared fixture for Hangfire
  state.
- NSubstitute cannot cleanly mock `IAsyncEnumerable`; use
  `FakeMetadataProvider` in `tests/Helpers/`.
- `Title.Name` is excluded from the metadata cascade; never overwrite it from
  enrichment providers.
- Pre-existing noise: backend builds may emit MSB3492 cache warnings. Do not
  fix that unless the touched change made it new.
- A full-suite integration failure with `Cannot access a disposed object ...
  'IServiceProvider'` is the `JobActivator.Current` leak, not noise. Follow the
  matching entry in `docs/known-issues.md`.

## EF Core Migrations

The application database is PostgreSQL 18. `src/Romd.Persistence/Migrations`
holds the squashed Npgsql baseline, `20260903203657_PostgreSqlBaseline`, and
the Npgsql migrations added since it. There is no SQLite provider or migration
chain; do not add one. Only the worker migrates: the
hosted `PostgreSqlSchemaProvisioner` (or `--migrate-database`) applies
migrations as `romd_provisioner`, lays down grants, and publishes
`romd.romd_schema`; API hosts read that version and fail readiness closed.
Bump `PostgreSqlConfiguration.ExpectedSchemaVersion` with every new migration.

```bash
dotnet ef migrations add <Name> \
  --project src/Romd.Persistence \
  --startup-project src/Romd.Worker.Host \
  --output-dir Migrations
```
