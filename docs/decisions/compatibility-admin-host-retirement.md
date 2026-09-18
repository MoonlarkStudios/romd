# Compatibility Admin Host Retirement

Status: implemented by #171

## Context

ROMD has one shared mapped admin HTTP composition but two executable admin
hosts: `Romd.Admin.Host`, which is part of the supported split-host deployment,
and `Romd.Host`, retained as a compatibility executable. They map the same
admin API, OpenIddict endpoints, static application, and realtime hubs.

Their bootstraps are not operationally identical. The canonical split admin
host additionally configures forwarded headers, a persisted data-protection
keyring, and a readiness marker, and it is the source of the `admin-v1` OpenAPI
document. The compatibility host retains an older OpenAPI name/generation path.
This drift strengthens the case for one executable; it is not behavior that
must be preserved from the compatibility host.

No repository documentation, Compose service, client, or deployment artifact
names a consumer that requires `Romd.Host`. The production and development
topology already uses `Romd.Admin.Host`, `Romd.Consumer.Host`, and
`Romd.Worker.Host`. The compatibility executable owns no distinct durable data
or behavior.

Keeping both executables is not free. Every startup, authorization, OpenAPI,
readiness, realtime, configuration, and migration-boundary change must prove
the same behavior twice. `Romd.Host` also leaves two apparent admin startup
projects while #128 needs exactly one application-database migration owner and
one canonical admin contract source for the PostgreSQL cutover.

## Decision

Retire `Romd.Host`. `Romd.Admin.Host` is the sole admin HTTP and admin realtime
deployable. `Romd.Consumer.Host` and `Romd.Worker.Host` retain their existing
ownership. `Romd.Hosting` remains the shared composition library.

The removal is a source/deployment compatibility break before v1, not a data
migration. First-party users replace:

```text
dotnet run --project src/Romd.Host
```

with:

```text
dotnet run --project src/Romd.Admin.Host
```

They may keep the same admin origin and environment/shared configuration. Both
executables use the same `romd-admin` audience, signing material, database, and
mapped application composition, so no identity or persistence conversion is
required.

Project-local configuration is not discovered across project roots. A source
deployment that stores overrides in
`src/Romd.Host/appsettings.Development.json` or another compatibility-host
`appsettings*` file must move those values to environment/shared configuration
or the corresponding `Romd.Admin.Host` configuration before switching. This
includes secrets, provider credentials, import roots, and other local options;
the old project file must not be assumed portable merely because the option
names are unchanged.

Removal is direct. ROMD will not ship a wrapper executable, redirect, duplicate
container target, or temporary contract-generation path. The rollback is the
previous ROMD application release.

## Removal Boundary

Issue #171 owns the implementation and verification:

- remove the compatibility executable, solution/run entries, and its legacy
  OpenAPI-generation task;
- delete compatibility-only tests while preserving Admin/Consumer/Worker host
  coverage;
- give the shared host integration suite a topology-neutral project identity;
- update runbooks, configuration guidance, architecture inventories, and CI;
- characterize compatibility-host project-local configuration and document
  moving every required override to the canonical admin host or environment;
- prove the canonical admin schema/client remains reproducible and the consumer
  schema/client remains byte-identical; and
- add a guardrail against reintroducing a fourth compatibility host or legacy
  generated-contract path.

Historical `Romd.Host.*` namespaces inside `Romd.Hosting` do not themselves
create a deployable or runtime boundary. They are not mechanically renamed as
part of retirement unless a focused guardrail demonstrates that a rename adds
architectural value without obscuring the executable removal.

The removal must land before #128 starts changing application-database startup,
migration ownership, and integration fixtures. It does not block the
Hangfire-only PostgreSQL step in #127, but completing it first keeps the whole
#126 campaign on one documented host topology.

## Rejected Alternatives

### Support `Romd.Host` indefinitely

Rejected because no consumer or behavior distinguishes it. Parity tests reduce
drift but do not remove duplicate build, startup, security, OpenAPI, and
migration-sensitive surfaces.

### Time-box retirement until after PostgreSQL

Rejected because #128 is exactly where an extra startup project is most costly.
Carrying the compatibility executable into the Npgsql baseline would require
proving and documenting a migration role that it should never own, then deleting
that work immediately afterward.

### Keep a wrapper executable or redirect

Rejected because the only known users are first-party and move repo-atomically.
A shim would preserve the maintenance burden and need its own later retirement
condition without protecting any named consumer.

### Rename all shared `Romd.Host.*` namespaces now

Rejected as unrelated mechanical churn. Executable ownership is determined by
projects, registrations, and mapped surfaces, not by historical namespace text.
Namespace cleanup can be proposed separately if it yields a measurable
guardrail or comprehension benefit.

## Consequences

- The supported process map becomes exactly Admin, Consumer, Worker, and the
  static browser player.
- Admin OpenAPI has one executable source and one generated-client path.
- PostgreSQL migration ownership and integration-fixture topology become less
  ambiguous before #128.
- Direct users of the compatibility executable must update their project path
  and relocate project-local configuration; no data, URL, token-audience, or
  API-contract migration is required.
- The architecture still permits shared host composition in `Romd.Hosting`;
  retirement does not collapse the split hosts or move endpoint ownership.

## Verification

The implementation removes the compatibility executable and legacy contract
path, moves the canonical configuration and EF tooling ownership, and preserves
Admin/Consumer/Worker coverage under the topology-neutral integration project.
Backend, integration, generated-contract, and web gates verify the three-host
topology.
