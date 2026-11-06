# AGENTS.md - ROMD Backend

.NET backend, contracts, storage, and backend tests. Use the task table and
relevant sections of [the .NET standards](../docs/development/dotnet-development.md)
for design and review; load linked owners when their contracts are affected.

## Required task routing

| Change | Read before editing |
| --- | --- |
| Mutations, transactions, events, jobs or cancellation | [Transaction/event decision](../docs/decisions/admin-use-case-transaction-event-boundaries.md) and [mutation patterns](../docs/architecture/backend-mutations.md) |
| HTTP behavior, auth, IDs, errors, pagination or concurrency | [API policy](../docs/decisions/api-contract-policy.md) and `.agents/skills/romd-web-api-client/SKILL.md` |
| Host ownership, public origins, worker registration or deployment composition | [Split-host topology](../docs/architecture/split-host-topology.md) |
| Schema or provisioning | [PostgreSQL decision](../docs/decisions/postgresql-single-provider.md) and [schema workflow](../docs/development/dotnet-development.md#postgresql-schema) |
| Media eligibility, encoding or verification | [Media processing](../docs/self-hosting/media-processing.md) |
| Native RID, libchdr pin or build recipe | [CHD decoder upgrades](../docs/self-hosting/production-deployment.md#chd-decoder-upgrades) |
| Backend tests | `tests/AGENTS.md` |

## Project map

- `Romd.Domain`: entities, value objects and domain policies; no external dependencies.
- `Romd.Application.Common`: shared CQRS, IDs, pagination, jobs and security ports.
- `Romd.Admin.Application` / `Romd.Consumer.Application`: use cases, read models
  and ports for their respective surfaces.
- `Romd.Contracts.Common` / `.Management` / `.Consumer`: shared and surface DTOs.
- `Romd.Persistence`: EF Core context, repositories, queries, migrations and
  seeders; no ASP.NET Core or Hangfire dependency.
- `Romd.Infrastructure`: external providers/tools, Hangfire, identity, delivery,
  readiness and realtime adapters.
- `Romd.Storage`: standalone content-addressable storage.
- `Romd.Media`: managed media readers/encoders over Storage; CHD uses
  `Moonlark.Libchdr` and verified native builds; ships no native binary.
- `Romd.Media.Worker`: supervised media executable; directly references only
  Media, without host configuration.
- `Romd.Hosting`: shared composition, Minimal API endpoints, auth and SignalR.
- `Romd.Admin.Host` / `Romd.Consumer.Host` / `Romd.Worker.Host`: production hosts.

## Implementation constraints

- Compile-time dependencies point inward. Application owns ports; Persistence
  and Infrastructure implement them; host composition wires adapters.
- Keep each CQRS use case under
  `Romd.{Admin|Consumer}.Application/{Area}/{Commands|Queries}/{UseCaseName}/`.
  Handlers implement `ICommandHandler<TCmd, TResult>` or
  `IQueryHandler<TQuery, TResult>` with
  `Task<ErrorOr<T>> HandleAsync(T input, CancellationToken ct = default)`.
  Expected rejections use existing `*Errors` factories; guards, infrastructure
  faults and cancellation follow the standards' [error rules](../docs/development/dotnet-development.md#errors-and-validation).
- Minimal API endpoint maps live in `Romd.Hosting/Endpoints/` as static
  `MapXxxEndpoints` extensions. Endpoint behavior uses application ports.
- Prefer pure policies for decisions; domain operations preserve invariants,
  own retained collections and receive behavioral time explicitly. Keep public
  surfaces narrow; expose no mutable backing state.
- Persistence ports return domain models or application projections; EF entities
  and `IQueryable` stay behind persistence. `RomdDbContext` defaults to NoTracking:
  mutating reads must use `.AsTracking()`.
- `Title.Name` is excluded from metadata enrichment; never overwrite it from providers.
- Consumer contract changes include the handwritten console client and its
  analysis/tests under `clients/romd_console/AGENTS.md`.
- Encoding returns raw artifact metadata; separate verification establishes
  logical evidence. Only the worker encodes. Native recipe/RID changes require
  re-measurement before stored CHDs are servable; use the routed upgrade procedure.
- `.editorconfig` owns syntax/format preferences. Use the standards for
  constructors, value types, async, resource ownership and performance choices.

## Validation

Use the [root validation table](../AGENTS.md#validation); backend work selects
`dotnet:style` and `test`, with integration/contract routes for the listed changes.
The style gate covers complete changed files; see its
[scope and base selection](../docs/development/dotnet-development.md#examples-and-verification).
[Development setup](../docs/development/getting-started.md) owns prerequisites,
worktree isolation, local hosts and rerunning registered checks. Search known
issues and the archive as root guidance requires before retrying unexpected failures.

## Fresh PostgreSQL Baseline

Only the worker provisions; APIs fail readiness closed. Baseline SQL resources
are immutable and hash-pinned; subsequent schema changes need a new migration.
Before schema/provisioning work, read the complete
[schema workflow](../docs/development/dotnet-development.md#postgresql-schema),
including supported databases, runtime grants and pinned EF tooling.
