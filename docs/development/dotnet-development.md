# .NET development standards

Use the task table and relevant sections when designing, implementing or reviewing
backend changes; expand to linked owners when their contracts are affected.
This guide defines the quality bar for changed behavior; existing code is an
example to inspect, not proof that every rule is satisfied. Keep unrelated
product refactors separate from guidance and tooling changes.

Contract and invariant rules below are requirements. Preferred patterns are
defaults that can be changed with a concrete reason in the pull request.
Optional techniques, including property-based testing and performance tuning,
need a demonstrated benefit. Scale design and verification to the risk.

## Start with the owning boundary

Read the relevant owners before changing their behavior:

| Change | Required reading |
| --- | --- |
| Mutations, transactions, events, jobs or cancellation | [Transaction and event decision](../decisions/admin-use-case-transaction-event-boundaries.md), then [backend mutation patterns](../architecture/backend-mutations.md) |
| HTTP contracts, authorization, IDs, errors, pagination or concurrency | [API contract policy](../decisions/api-contract-policy.md) and the [API client workflow](../../.agents/skills/romd-web-api-client/SKILL.md) |
| Host composition, admin/consumer ownership or worker registration | [Host ownership decision](../decisions/plane-ownership.md) and [split-host topology](../architecture/split-host-topology.md) |
| Schema or persistence policy | [PostgreSQL decision](../decisions/postgresql-single-provider.md) and [schema workflow](#postgresql-schema) |

These documents own their contracts. This guide supplies implementation and
review patterns without replacing those rules. A consumer contract change
includes the console's handwritten Dart client and console checks, even when
the generated TypeScript drift check passes.

## Dependencies and feature ownership

Compile-time dependency direction is explicit: application projects depend on
Domain; Persistence and Infrastructure implement application-owned ports and
depend inward. Domain has no dependency on application, host, persistence or
external frameworks. Host composition roots connect adapters to ports;
endpoint behavior uses application contracts rather than adapter types.

Organize use cases by capability within these boundaries. Keep related commands,
queries and policies discoverable under their owning feature. Shared code needs
shared semantics and ownership, not just similar syntax. Keep interfaces focused
on the capability their caller needs; an interface should protect a boundary,
encapsulate complexity or support meaningful substitution. Prefer composition
over inheritance unless the hierarchy expresses a stable relationship.

Expose only the types and members needed across an assembly or feature boundary.
Use `internal` implementations when composition permits it and `sealed` by
default. Add project or module boundaries when they enforce a needed dependency
rule, rather than to reach a prescribed number of layers.

## Contract type names

Types in `Romd.Contracts.*` omit the `Dto` suffix; their project and namespace
already identify them as contracts. Name each type for its meaning and shape,
such as `Library`, `CollectionItem`, `SystemSummary`, or `ConsumerTitleDetail`.
Keep meaningful qualifiers and suffixes, including `Consumer`, `Request`,
`Response`, `Summary`, `Detail`, and `Manifest`; do not replace `Dto` with a
blanket `Response` or `Contract` suffix.

Contract types remain separate from domain and persistence types even when
their names match. Resolve ambiguous names with explicit namespace or type
aliases at mapping boundaries. Renames must update backend references, OpenAPI
snapshots, generated clients, and affected callers together through the
[API client workflow](../../.agents/skills/romd-web-api-client/SKILL.md), while
preserving JSON member names, enum values, and discriminator values unless a
wire change is explicitly intended.

## Pure decisions and explicit effects

Prefer a functional core with imperative orchestration:

1. Read the facts needed by a use case through its ports.
2. Decide with a pure policy or an invariant-preserving domain operation.
3. Revalidate assumptions that can change, then commit the mutation and durable
   work intents through the owning transaction boundary.
4. Execute required external effects through the durable dispatch mechanism.

A pure policy depends on explicit inputs, returns a decision or plan, and does
no database, filesystem, network or process I/O. It does not read ambient user
state or the system clock. Obtain time from `TimeProvider` or the existing
clock in orchestration and pass the timestamp to domain behavior. Supply other
nondeterministic inputs explicitly when they affect decisions or test outcomes.
Pure policies can be static functions; they do not need a service and interface
solely to be injected.

Keep exact timestamp facts in domain and in-memory result assertions. The backend
[`DateTimeOffset` converters](../../src/Romd.Persistence/RomDbContext.cs) store values
at Unix-millisecond precision; compare database readbacks with that round-trip
value. Retain finer-precision inputs and assertions where no persistence conversion
occurs. Do not change the stored representation to make a test pass.

Use immutable facts and results when callers need stable values. Local variables,
loops and mutation inside an algorithm are appropriate when they keep the
implementation clear or avoid measured allocation costs. Functional style does
not require replacing streaming or buffer-oriented media code with LINQ.

## Encapsulation and valid state

An aggregate owns the operations needed to preserve its invariants. Public
setters and child mutators must not let callers bypass those operations.
Validate a complete transition before changing state so a rejected operation
leaves the object valid. A mutable aggregate with guarded behavior is a useful
default; immutable snapshots and plans serve different needs.

Own retained collection inputs. Copy caller-owned mutable collections, and
expose a read-only view or immutable value that cannot be cast back to the
aggregate's mutable backing collection. Consider mutable elements as well as
the collection itself. `IReadOnlyList<T>` limits access through an interface;
it does not establish immutability. A record or `init` property also gives only
shallow immutability when it references mutable objects.

Use value objects where identity, units, validation or equality prevent likely
mistakes, such as hashes and storage keys. Prefer explicit outcome variants or
validated types when nullable fields, strings or independent booleans permit
contradictory states. Define allowed state transitions and rejection outcomes.
Preserve persisted and wire representations through deliberate mappings and
migrations; stronger internal types do not authorize renaming contract values.
Check the default value and equality semantics before choosing a struct or
record, especially when it contains collections.

## Errors and validation

| Situation | Handling |
| --- | --- |
| Expected business rejection, conflict or missing state | Return an explicit result; handlers use `ErrorOr<T>` and the existing `*Errors` factories. |
| Programmer misuse or an impossible internal state | Fail explicitly with a guard or exception. |
| Infrastructure failure | Preserve the cause and let the established boundary translate or report it; do not turn every exception into a business rejection. |
| Cancellation | Propagate the token and preserve cancellation semantics rather than reporting an ordinary failure or retrying blindly. |

Keep the domain independent of HTTP and application result libraries. A domain
operation can return a focused outcome, `Try` result or domain result type as
needed; the handler translates it to the application error contract.

Name validation ownership. Request binding and validators check input shape;
domain behavior enforces business invariants regardless of the caller;
database constraints protect persisted and concurrent integrity. Rechecking an
invariant at another trust or concurrency boundary is deliberate defense.
Avoid copying the same policy independently into endpoints, handlers and stores.

## Writes and reads

Use the [mutation guide](../architecture/backend-mutations.md) for short
handler-owned transactions, staged writes, revision checks and durable work.
External preparation finishes outside a database transaction; revalidate the
facts a write depends on when entering its protected boundary. The
[locked CAS publication protocol](../architecture/backend-mutations.md#locked-cas-publication)
defines the required storage effect under file locks. Automatic retries need
an idempotent operation and an explicit budget. A cancellation or lost response
after commit does not mean the durable mutation was rolled back.

Read ports return bounded application projections or domain values appropriate
to their use case. A query does not need to load a full aggregate to display a
list or detail screen. EF entities and `IQueryable` stay behind persistence
ports. Keep authorization and consistency promises explicit in projections.
Use keyset pagination by default; the API policy owns the narrow exceptions.
Measure query shape, round trips and allocations before adding parallelism or
caching. A scoped `DbContext` runs one operation at a time, and mutating reads
use `.AsTracking()` because ROMD defaults to no tracking.

## PostgreSQL schema

The application database is PostgreSQL 18. `src/Romd.Persistence/Migrations`
contains one fresh-install baseline, `20261031050000_FreshMediaBaseline`.
It installs the guarded media fact resources alongside the remaining application
schema. The additive `20261102000000_PspUmdObservations` migration adds nullable
PSP observations without backfilling evidence; current readiness is version 73.
The SQL resources executed by the baseline are immutable and SHA-256 pinned by
`FreshMediaBaselineResourceTests`;
subsequent schema changes require a new migration. `RuntimeFactGrants.sql` is
separate startup policy, reapplied on every provision, and must stay idempotent.
Unsupported existing or unversioned application schemas are refused before any schema writes. Legacy upgrades,
evidence conversion, backfills and baseline downgrades are unsupported.

Only the worker provisions: `PostgreSqlSchemaProvisioner` (or
`--migrate-database`) runs as `romd_provisioner`, applies pending migrations, narrows
runtime grants and publishes `romd.romd_schema`. API hosts read that version and
fail readiness closed. Preserve unrelated schema constraints and functions when
adding migrations; SQL-owned media tables are excluded from EF scaffolding.

Restore the repository-pinned EF CLI before schema work. Keep
`.config/dotnet-tools.json` aligned with `Directory.Packages.props`; do not use
the global CLI. Check the model with:

```bash
mise run tools:restore
dotnet ef migrations has-pending-model-changes \
  --project src/Romd.Persistence \
  --startup-project src/Romd.Worker.Host
```

## .NET implementation practices

- Use async APIs through the I/O call chain and propagate cancellation tokens.
  Avoid blocking on tasks, `async void`, and detached work that captures scoped
  services. Bounded concurrency needs independent state/scopes and a stated
  consistency contract.
- Inject dependencies explicitly. Keep service resolution in composition or
  established scope factories, and preserve DI lifetime ownership. A singleton
  must not retain scoped services. Dispose owned resources with `using` or
  `await using`; the caller does not dispose injected services it does not own.
- Use the existing configured HTTP clients and resilience policies. Define
  timeouts, cancellation and retry ownership together; avoid layered retries
  and replaying non-idempotent requests. Stream large responses and dispose
  responses/streams according to their ownership contract.
- Use structured logs with stable event meaning and useful correlation. Keep
  secrets, tokens and private media data out of logs. Report failures at the
  owning boundary so retries do not generate duplicate or misleading errors.
- Prefer primary constructors for straightforward dependency capture and records
  for data with appropriate value semantics. Use explicit constructors when
  validation, initialization or ownership is clearer that way.
- Let names and control flow explain behavior. Extract methods around meaningful
  concepts, keep responsibilities cohesive, and comment on reasons, invariants
  and trade-offs. Choose pattern matching, LINQ or loops for clarity. Avoid
  arbitrary method-size rules and speculative generic frameworks.
- `.editorconfig` owns formatting. Use repository validation tooling and the
  pinned SDK; do not add a competing formatter or silence warnings for a pass.
  Optimize hot media paths with streaming, spans and pooled buffers when the
  ownership and measurements justify the complexity.

## Examples and verification

These examples demonstrate particular patterns, not blanket templates:

| Example | Pattern to inspect |
| --- | --- |
| [Provider identity policy](../../src/Romd.Domain/Catalog/ProviderIdentityPolicy.cs) | Owned immutable evidence, explicit search completeness and trusted bridges produce constrained deterministic outcomes. |
| [Title metadata resolution](../../src/Romd.Domain/Catalog/TitleMetadataResolutionPolicy.cs) | Typed captured facts produce a complete result before the aggregate applies effective fields, provenance and ratings. |
| [Add collection title](../../src/Romd.Admin.Application/Collections/Commands/AddTitle/AddTitle.cs) | Short transaction ownership, locked mutation, staged write and response read. |
| [Consumer title query](../../src/Romd.Consumer.Application/Browse/Queries/GetConsumerTitle/GetConsumerTitle.cs) | Caller scope, purpose-built read result and explicit outcome mapping. |
| [Container facts](../../src/Romd.Domain/Source/Rom/RomContainerFacts.cs) | Guarded construction, distinct variants and owned immutable captured metadata. |
| [Title encapsulation tests](../../tests/Romd.Domain.Tests/Catalog/TitleEncapsulationTests.cs) | Checks for external collection mutation and aggregate-bypassing child methods. |

Test observable behavior and invariant preservation, including rejected
transitions. Pure-policy tests should need no database or mock graph. Use table
cases or property-based tests for properties such as normalization idempotence,
ordering permutations, round trips and state transitions when they cover risks
better than examples alone. A new test library is an optional dependency choice.
Keep real PostgreSQL tests for locking, constraints, transaction atomicity and
recovery; mocks do not prove those guarantees. Architecture checks should pin
important dependency/capability boundaries while permitting internal refactoring.

The [domain time guard](../../tests/Romd.Infrastructure.Tests/Architecture/DomainTimeArchitectureTests.cs)
checks maintained source for ambient framework clock reads and `TimeProvider`
dependencies. The [transaction capability ledger](../../tests/Romd.Infrastructure.Tests/Persistence/RepositoryTransactionOwnershipArchitectureTests.Capabilities.cs)
records direct save/transaction sites across adapter types, with remaining
migration debt separate from reviewed worker, snapshot and batch protocols.
Both checks have explicit limits; validate runtime ownership and behavior too.

[Root validation routing](../../AGENTS.md#validation) selects checks, and
[test instructions](../../tests/AGENTS.md) explain test ownership. A style or
architecture scan cannot establish runtime correctness. In review, verify the
changed behavior, its boundaries, collection ownership, failure/cancellation
paths and meaningful test evidence; distinguish a preferred pattern from a
contract requirement.

Run `mise run dotnet:style -- --base origin/main` from the root for changed C#
files. The SDK whitespace formatter checks complete maintained files changed
since the base, including local changes, and excludes generated output. Pin
`--base` to a revision when citing reproducible evidence. An `.editorconfig`
change broadens the checked files conservatively. This gate does not certify
untouched files, enforce IDE style suggestions or turn general analyzer warnings
into errors; backend builds and behavior checks remain separate.

For a newly created Git ref, CI passes
the all-zero previous object ID. This checks every maintained C# file without
requiring a parent commit. Reproduce that selection with
`mise run dotnet:style -- --base 0000000000000000000000000000000000000000`.
A missing named base, missing commit or unrelated history still fails closed.

Further reference: Microsoft's [architectural principles](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/architectural-principles),
[domain validation](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-model-layer-validations),
[CQRS reads](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/cqrs-microservice-reads)
and [record immutability](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record#immutability).
