# Admin Use-Case, Transaction, And Domain-Event Boundaries

Status: accepted

Related issues: #75, #77, #85, #86, #87, #88

## Context

ROMD's admin backend already has the right skeleton: Clean Architecture
layers, an established CQRS shape (`ICommandHandler<TCommand, TResult>`
returning `ErrorOr<T>`,
`src/Romd.Application.Common/Common/Cqrs/ICommandHandler.cs:10`), an
application-owned transaction port
(`src/Romd.Admin.Application/Common/Persistence/IUnitOfWork.cs`), and a
durable admin realtime outbox with a dispatcher on the admin hosts
(`docs/split-host-topology.md`, "Admin Realtime Relay").

The boundaries around that skeleton have drifted:

- Endpoints orchestrate business logic. `LibraryEndpoints.Create`
  (`src/Romd.Hosting/Endpoints/LibraryEndpoints.cs:91-121`) performs request
  validation, domain construction, `repository.AddAsync`, realtime outbox
  enqueue, and materialization scheduling in the endpoint body, and injects
  the Infrastructure-owned `IAdminRealtimeOutbox`
  (`src/Romd.Infrastructure/Realtime/IAdminRealtimeOutbox.cs`) directly into
  Hosting.
- Transaction ownership is inconsistent. Six command handlers and two
  services commit through `IUnitOfWork`; most other command paths rely on
  repositories that save implicitly, and `DeleteDatCommandHandler` runs a
  four-commit chain
  (`src/Romd.Admin.Application/Source/Dat/Commands/DeleteDat/DeleteDat.cs:74-86`).
- Event durability depends on which host runs the code. The API host
  transport registration binds `IJobNotifier`/`IStatsNotifier` to direct
  SignalR sends
  (`src/Romd.Hosting/Hosting/RomdHostRegistrationExtensions.cs:422-425`),
  the worker binds them to the durable outbox
  (`src/Romd.Infrastructure/DependencyInjection.cs:387-395`), and SignalR
  send failures are logged and swallowed
  (`src/Romd.Hosting/Hubs/SignalRStatsNotifier.cs:13-23`).
- Eight `_ = Task.Run(...)` sites plus one discarded-task variant detach
  notification work from the request/job lifetime while capturing scoped
  services.
- Several read paths run parallel queries against a single scoped
  `RomdDbContext`.

This ADR fixes the use-case, transaction, and domain-event boundaries for
the admin surface. The standard covers both request mutations and Hangfire
workflows. Its contract-side counterpart is
`docs/decisions/admin-api-contract-policy.md`, which defines the public
async-operation contract (202 + job id + `Location`); this ADR governs the
in-process semantics behind that contract and does not restate it.

## Decisions

### Use-Case Ownership: One Command Handler Per User-Observable Mutation

Every user-observable mutation is owned by exactly one application command
handler implementing `ICommandHandler<TCommand, TResult>` in the
established CQRS directory shape
(`src/Romd.Admin.Application/{Area}/Commands/{UseCaseName}/`).

Endpoints only bind input, apply authorization metadata, map contracts to
commands and results to responses, dispatch to the handler, and translate
`ErrorOr` results into HTTP results.

Named current violations, migrated under #86:

- `LibraryEndpoints` lifecycle: `Create`, `Update`, `Delete`,
  `ForceMaterialize`
  (`src/Romd.Hosting/Endpoints/LibraryEndpoints.cs:91`, `:123`, `:164`,
  `:190`).
- `UserEndpoints` lifecycle: `Create`, `AssignRole`, `AssignLibrary`,
  `AssignDefaultLibrary`
  (`src/Romd.Hosting/Endpoints/UserEndpoints.cs:106`, `:264`, `:308`,
  `:367`). No command handlers exist for these paths; the endpoints drive
  `UserManager<RomdUser>` and `ILibraryRepository` directly, and the
  `src/Romd.Admin.Application/Users/Commands/` directories are empty
  scaffolding.
- `EnrichmentEndpoints.SetFieldOverrides`
  (`src/Romd.Hosting/Endpoints/EnrichmentEndpoints.cs:133-176`): loads the
  title, mutates field-source overrides, rematerializes metadata, flags
  libraries, and persists, all in the endpoint body.
- `TitleEndpoints.TriggerEnrichment`
  (`src/Romd.Hosting/Endpoints/TitleEndpoints.cs:183-201`): domain mutation
  plus repository update plus scheduler enqueue in the endpoint body.

`LibraryEndpoints` injecting `IAdminRealtimeOutbox` — an Infrastructure
concern — into Hosting (`LibraryEndpoints.cs:95`) is a boundary violation.
It is resolved by "Domain Events Are Transactional Outbox Intents" below:
the endpoint stops touching the outbox because the handler records the
event.

### Transaction Ownership: The Command Handler Owns One Commit

The command handler owns the transaction through the application-owned
`IUnitOfWork` port
(`src/Romd.Admin.Application/Common/Persistence/IUnitOfWork.cs`,
implemented by
`src/Romd.Infrastructure/Persistence/EfUnitOfWork.cs`). There is exactly
one commit for the primary user-observable mutation. Repositories on
command paths stop calling `SaveChangesAsync` implicitly.

The port is already in use at eight sites: the `IngestDat`, `IngestRom`,
`AssignPlatform`, `BackfillBiosCatalog`, `MergeTitles`, and `MoveGame`
command handlers plus `TaxonomyService` and `FileStorageService`.
`IngestRomCommandHandler` shows the conforming shape: repository writes
happen inside the transaction and become durable at the handler's single
`transaction.CommitAsync`
(`src/Romd.Admin.Application/Source/Rom/Commands/IngestRom/IngestRom.cs:137`).

Migration is per-slice. Each path migrates as #85, #86, and #88 touch it.
New and refactored code complies immediately. There is no big-bang
repository rewrite. The guardrail is an architecture test with an
explicit allowlist of not-yet-migrated paths — endpoint classes still
referencing Infrastructure types, and repositories still committing
implicitly. Each migration slice removes entries; the test fails if the
allowlist grows or a removed entry regresses.

Multi-commit chains are non-conforming. `DeleteDatCommandHandler` commits
four times: repository delete (`DeleteDat.cs:74`), CAS cleanup
(`DeleteDat.cs:75`), catalog rebuild (`DeleteDat.cs:82`), and library
flagging (`DeleteDat.cs:85`). Multi-commit chains decompose under the
migrating issue into (a) one atomic commit containing the primary
mutation, its dependent flag updates, and its event intents, and (b)
recoverable post-commit work under the "Post-Commit Work Dispatch" rule
for long-running derived work. For `DeleteDatCommandHandler`
specifically: the atomic commit is the DAT deletion plus library
rematerialization flags plus event intents; the catalog rebuild becomes
recoverable post-commit work whose need is derivable from committed
state; CAS cleanup becomes retention-aware per the DAT-lineage policy.
#86 owns the DeleteDat decomposition; #88 owns the retention policy.

### Domain Events Are Transactional Outbox Intents

A use case records required admin events by adding outbox rows in the same
transaction/`SaveChanges` as the business mutation. There is no MediatR, no
generic event bus, and no in-process domain-event dispatcher. The outbox
row is the event; the dispatcher is the only fan-out.

The outbox row's existing `EventType` column
(`src/Romd.Infrastructure/Persistence/Entities/AdminRealtimeOutboxEventEntity.cs:8`)
is the event type discriminator. Each event payload schema additionally
carries an integer schema version, recorded on the outbox row and
delivered to clients with the event. A payload schema change bumps the
version and follows the repo-atomic compatibility rule in
`docs/decisions/admin-api-contract-policy.md` — backend payload, client
consumption, and call sites change together. #85 implements the storage
and delivery mechanics.

Non-conforming today: `AdminRealtimeOutbox.EnqueueAsync`
(`src/Romd.Infrastructure/Realtime/AdminRealtimeOutbox.cs:16-31`)
immediately self-commits with its own `SaveChangesAsync`. It must support
enlistment in the caller's unit of work so the event row and the business
mutation become durable in one commit.

The reference pattern is already in-tree: `HangfireJobStateSyncFilter`
writes the ROMD job row and the outbox row in one `SaveChanges`
(`src/Romd.Infrastructure/Jobs/HangfireJobStateSyncFilter.cs:45-62`).

### Uniform Event Routing Through The Durable Outbox

Required admin events in all hosts flow through the durable outbox. The
SignalR notifiers (`SignalRJobNotifier`, `SignalRStatsNotifier` in
`src/Romd.Hosting/Hubs/`) become delivery adapters used only by the outbox
dispatcher.

This removes the current host-dependent durability split: the API host
transport binds `IJobNotifier`/`IStatsNotifier` to direct best-effort
SignalR (`RomdHostRegistrationExtensions.cs:422-425`), the worker binds
them to the durable outbox (`DependencyInjection.cs:387-395`), and the
baseline registration is a no-op (`DependencyInjection.cs:381-382`). The
same handler — `DeleteDatCommandHandler`, for example — therefore has
different event-loss semantics depending on which host runs it. #85
implements the uniform routing.

### No Detached Fire-And-Forget Work

No detached fire-and-forget tasks capturing scoped services. The event is
recorded in the commit; delivery belongs to the dispatcher. The following
sites are eliminated by the two decisions above (#85 implements):

- `src/Romd.Admin.Application/Source/Dat/Commands/DeleteDat/DeleteDat.cs:91`
- `src/Romd.Admin.Application/Source/Rom/Commands/BatchDelete/BatchDelete.cs:142`
- `src/Romd.Admin.Application/Source/Rom/Commands/PurgeUnidentified/PurgeUnidentified.cs:62`
- `src/Romd.Admin.Application/Source/Rom/Commands/DeleteRom/DeleteRom.cs:65`
- `src/Romd.Admin.Application/Titles/Commands/UploadTitleMedia/UploadTitleMedia.cs:104`
- `src/Romd.Admin.Application/Titles/Commands/DeleteTitleMedia/DeleteTitleMedia.cs:80`
- `src/Romd.Infrastructure/Jobs/Executors/ReplaceDatJobExecutor.cs:131`
- `src/Romd.Infrastructure/Jobs/Executors/UploadJobExecutor.cs:117`
- the discarded-task variant `_ = NotifyStatsChangedAsync()` in
  `src/Romd.Admin.Application/Source/Rom/Commands/IngestRom/IngestRom.cs:144`

### Worked Example: Library Creation (Request Mutation)

Current shape: the endpoint body at
`src/Romd.Hosting/Endpoints/LibraryEndpoints.cs:91-121` validates the
request, maps and validates configuration, constructs the `Library`
aggregate, calls `repository.AddAsync` (which saves), enqueues a
`LibraryUpdated` outbox row in a second, separate commit, and calls
`materializationScheduler.EnqueueIfNeededAsync`. A crash between the add
and the enqueue loses the event; the endpoint owns business orchestration
and an Infrastructure type.

Target shape: one command handler, one commit, the outbox intent recorded
in that same commit.

```csharp
// src/Romd.Admin.Application/Libraries/Commands/CreateLibrary/CreateLibrary.cs
public sealed class CreateLibraryCommandHandler(
    ILibraryRepository libraries,
    // To-be-created application-owned enqueue port (illustrative name);
    // #85 adds it to Romd.Admin.Application and it enlists in the caller's
    // unit of work. Today's IAdminRealtimeOutbox lives in Infrastructure.
    IAdminEventOutbox outbox,
    ILibraryMaterializationScheduler materializationScheduler,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreateLibraryCommand, Library>
{
    public async Task<ErrorOr<Library>> HandleAsync(
        CreateLibraryCommand command, CancellationToken ct = default)
    {
        // Validation and reference checks -> ErrorOr validation errors.
        // Domain construction: Library.CreateNew(...), MarkAsDefault() when requested.

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var created = await libraries.AddAsync(library, ct);
        await outbox.EnqueueAsync(
            AdminRealtimeEventTypes.LibraryUpdated, ToPayload(created), ct);
        await transaction.CommitAsync(ct); // the single commit: row + event together

        // Post-commit acceleration only. The committed NeedsMaterialization
        // state is the durable source of truth the worker's materialization
        // dispatch acts on, so nothing is lost if this call never runs.
        await materializationScheduler.EnqueueIfNeededAsync(created.Id, ct);
        return created;
    }
}
```

The endpoint shrinks to its allowed responsibilities:

```csharp
private static async Task<IResult> Create(
    [FromBody] CreateLibraryRequest request,
    ICommandHandler<CreateLibraryCommand, Library> handler,
    CancellationToken ct)
{
    var command = MapToCommand(request); // contract -> validated command
    if (command.IsError)
        return Problem(command.Errors);

    var result = await handler.HandleAsync(command.Value, ct);
    return result.Match(
        library => Results.Created(
            $"/api/libraries/{IdCoder.Encode(library.Id)}", ToDto(library)),
        Problem);
}
```

The endpoint no longer references `Romd.Infrastructure` types, the event
cannot be lost after the mutation is durable, and the same handler runs
identically from a request or from a Hangfire workflow.

### DbContext Concurrency: Scoped Contexts Are Single-Flight

A scoped `RomdDbContext` is single-flight; sequential awaits are the rule.
Parallel reads require explicitly created separate scopes/contexts and an
explicit consistency statement. The default answer is sequential
execution: SQLite serializes access anyway, and read parallelization is an
optimization that requires #98 measurement first.

Current violations, migrated under #87:

- `SearchRepository.GetCatalogFiltersAsync`: an eight-way `Task.WhenAll`
  over aggregation queries on one context
  (`src/Romd.Infrastructure/Persistence/Repositories/SearchRepository.cs:714`).
- Four dashboard query handlers awaiting `Task.WhenAll` over repositories
  that resolve the same scoped context, under
  `src/Romd.Admin.Application/Dashboard/Queries/`:
  `GetSystemHealth/GetSystemHealth.cs:21`,
  `GetCoverageStats/GetCoverageStats.cs:22`,
  `GetSystemStats/GetSystemStats.cs:32`,
  `GetLibrarySummary/GetLibrarySummary.cs:24`.

Snapshot semantics are documented as follows: multi-query reads are
per-query consistent only; cross-query consistency is not promised.

### Long-Running Work: Resumable Phased Jobs

A long-running workflow is a resumable Hangfire job with persisted phase
state. This formalizes the existing checkpoint pattern —
`JobRunner<TJob>` persists the job and notifies on every
`JobContext.CheckpointAsync`
(`src/Romd.Infrastructure/Jobs/JobRunner.cs:34-42`), and `ReplaceDatJob`
already carries a phase enum
(`src/Romd.Domain/Jobs/ReplaceDatJob.cs:29-45`) — into a standard:

- an explicit phase enum persisted at each phase boundary;
- idempotent steps between boundaries;
- defined recovery semantics at every phase boundary;
- cancellation converges to a terminal or resumable state;
- operator-visible failure.

Automatic retries only wrap idempotent steps. A retry must never replay a
non-idempotent business mutation.

The current retry reality is vestigial: `JobRunner` catches all executor
exceptions, marks the job terminally failed, and does not rethrow
(`JobRunner.cs:64-74`), and re-entry returns immediately on terminal jobs
(`JobRunner.cs:23`), so configured Hangfire retries mostly no-op. The
phased standard replaces accidental non-retry with deliberate
resumability.

The public contract for these workflows is defined in
`docs/decisions/admin-api-contract-policy.md`.

### Worked Example: DAT Replacement Is The Reference Saga (Hangfire Workflow)

Current shape
(`src/Romd.Infrastructure/Jobs/Executors/ReplaceDatJobExecutor.cs`):

- Non-atomic ingest-then-delete: the executor ingests the new DAT
  (`ReplaceDatJobExecutor.cs:59-78`), then deletes the old DAT through
  `DeleteDatCommandHandler` (`ReplaceDatJobExecutor.cs:90-116`).
- A failed delete is recorded and swallowed — "don't fail - the new DAT is
  already ingested" (`ReplaceDatJobExecutor.cs:100-109`) — leaving both
  versions live with no marker of which is authoritative.
- `DatFile` has no active/superseded state
  (`src/Romd.Domain/Source/Dat/DatFile.cs`).
- The delete path destroys the superseded DAT's CAS blob immediately via
  `DeleteIfUnreferencedAsync` (`DeleteDat.cs:75`), erasing lineage.

Target shape (#88): a phased resumable saga with an explicit `DatFile`
active/superseded lifecycle state.

| Phase | Work | Recovery semantics at the boundary |
| --- | --- | --- |
| 1. Ingest new version | Ingest the replacement DAT as a new, not-yet-active version. | Restartable. The new version is inactive; catalog truth is unchanged. A crash leaves an inactive version that is safe to resume or discard. |
| 2. Activate/supersede | One commit flips the new version to active, the prior version to superseded, applies N=1 superseded retention, invalidates catalog/materialization state, and records required admin events. | Atomic. Before the commit the old version is active; after it the new version is active and at most the newest superseded version remains. Retention failure rolls the whole activation back for retry. |
| 3. Convergence backstop | The recurring worker sweep re-applies retention for crash/race repair and historical states. | Idempotent and re-runnable. Discovery is a safe read-only superset; deletion and invalidation share one mutation transaction. |

Target shape; names are illustrative — #88 owns final naming, schema, and
migration.

```csharp
public enum DatFileLifecycle { PendingActivation, Active, Superseded }

public enum ReplaceDatPhase { IngestNewVersion, ActivateSupersede, Cleanup }

// Phase 2: the saga's single atomic point.
private async Task ActivateAndSupersedeAsync(
    DatFile newVersion, DatFile priorVersion, CancellationToken ct)
{
    await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

    newVersion.Activate();    // PendingActivation -> Active
    priorVersion.Supersede(); // Active -> Superseded
    await datFiles.UpdateAsync(newVersion, ct);
    await datFiles.UpdateAsync(priorVersion, ct);
    await unitOfWork.FlushAsync(ct); // make the lifecycle winner visible in-transaction
    await datFiles.DeleteSupersededVersionsBeyondMostRecentAsync(
        newVersion.DatSourceId, ct);

    await outbox.EnqueueAsync(
        AdminRealtimeEventTypes.DatReplaced,
        ToPayload(newVersion, priorVersion), ct);

    // One commit: both lifecycle flips and the required admin event
    // intents become durable together.
    await transaction.CommitAsync(ct);
}

// The recurring sweep repeats retention as an idempotent convergence backstop.
```

Invariants:

- Prior lineage stays queryable and cannot be mistaken for active truth.
- CAS blobs are retained until no retained version references them. The
  current immediate destruction of the superseded DAT blob is
  non-conforming under this policy.
- Crash injection at each phase boundary must converge to a known repairable
  state; activation and normal retention share one rollback boundary.

### Cancellation Semantics

`CancellationToken` propagates through application and infrastructure
ports. The transaction commit is the atomicity point: before the commit,
cancellation rolls back the whole mutation; after the commit, the mutation
and its recorded outbox intents are durable, so cancellation or shutdown
after commit cannot lose a required event. Required post-commit effects
live in the outbox, never in the request or job lifetime.

### Post-Commit Work Dispatch

A Hangfire enqueue that follows a commit is acceptable only when the
committed state is the durable representation of the pending work and a
recovery path re-derives the dispatch from that state. The materialization
pattern is the model: the committed `NeedsMaterialization` flag is the
truth, and the worker's `MaterializationDispatcher` polls
`GetNeedingMaterializationAsync` on a 30-second cadence
(`src/Romd.Infrastructure/Libraries/MaterializationDispatcher.cs:53`), so
a lost enqueue delays the work but cannot lose it.

Where no recovery path exists, the work intent must be persisted in the
same commit — a job row or outbox intent — before any enqueue. An enqueue
call is acceleration, never the sole record of pending work.
Enqueue-after-commit paths whose committed flag has no recovery loop
today, such as the enrichment trigger's queued-state-plus-scheduler-enqueue
(`src/Romd.Hosting/Endpoints/TitleEndpoints.cs:183-201`), gain either a
recovery poll or a persisted intent as #86 migrates them. The migrating
slice states which mechanism the path adopts and proves it with a test;
neither option may be deferred silently.

### Read Models Stay Behind Application Ports

Repositories return domain models or purpose-built application read
models. EF entities and `IQueryable` never cross application ports. This
is the existing rule (`src/AGENTS.md`, "Repositories return domain models,
not EF entities"), restated here as ADR-governed. A guardrail test is
added when the rule is mechanically expressible.

### Time Is An Injected Dependency

Where time affects behavior or tests, use `TimeProvider` or the existing
clock abstraction rather than inline `DateTime.UtcNow` /
`DateTimeOffset.UtcNow`. `AdminRealtimeOutbox` and
`HangfireJobStateSyncFilter` already inject `TimeProvider`
(`AdminRealtimeOutbox.cs:9`, `HangfireJobStateSyncFilter.cs:17`); inline
stamps such as `DatFile.CreateNew`'s `DateTimeOffset.UtcNow`
(`DatFile.cs:96`) move to the injected pattern as the code they live in is
refactored.

## Rejected Alternatives

- MediatR or any generic mediator/event framework, in-process
  domain-event dispatcher, or event bus: rejected for lack of demonstrated
  need. Issue #89 requires avoiding a generic framework unless its benefit
  is demonstrated; the transactional outbox row plus the existing
  dispatcher covers every current admin event with fewer moving parts.
- Big-bang repository rewrite removing implicit `SaveChangesAsync`
  everywhere at once: rejected in favor of per-slice migration under #85,
  #86, and #88 enforced by the allowlist guardrail.
- Direct SignalR sends from handlers as the delivery model: rejected. It
  produces host-dependent event-loss semantics and swallowed failures.
- Fire-and-forget delivery (`_ = Task.Run(...)`) as a latency
  optimization: rejected. It captures scoped services beyond their
  lifetime and loses events on shutdown; durable delivery makes it
  unnecessary.
- One transaction spanning an entire DAT replacement: rejected. Ingest is
  long-running, and the single-writer SQLite deployment cannot hold a
  write transaction across it. The saga's activate/supersede step is the
  only atomic point.
- Parallel query execution on one scoped context as a default performance
  posture: rejected pending #98 measurement. `DbContext` is not
  thread-safe and SQLite serializes access.

## Implementation Consequences

- Endpoint bodies shrink to bind/authorize/map/dispatch/translate.
  `Romd.Hosting` endpoint signatures stop referencing
  `IAdminRealtimeOutbox` and other Infrastructure types.
- #85 introduces the application-owned enqueue port used by handlers;
  enlistment in the caller's unit of work replaces the self-commit in
  `AdminRealtimeOutbox.EnqueueAsync`. Infrastructure implements the port
  and retains claiming, dispatching, and cleanup. `Romd.Hosting` and
  application handlers stop referencing the Infrastructure outbox type.
- `IJobNotifier`/`IStatsNotifier` implementations become dispatcher-only
  delivery adapters; command handlers stop injecting notifiers and record
  outbox intents instead.
- Repositories on migrated command paths stop saving implicitly. Each
  #85/#86/#88 slice removes its paths from the allowlist guardrail so
  conformance is enforced, not aspirational.
- `DatFile` gains a lifecycle state column and EF migration. DAT-path
  callers of `FileStorageService.DeleteIfUnreferencedAsync` move to
  retention-aware cleanup.
- `JobRunner`'s swallow-and-terminate failure posture is replaced by
  phase-aware recovery for jobs adopting the resumable standard.
- The parallel read paths in `SearchRepository.GetCatalogFiltersAsync` and
  the four dashboard handlers become sequential under #87 unless #98
  measurement justifies explicit multi-scope parallelism with a stated
  consistency contract.
- New and refactored code complies with every decision immediately;
  existing code migrates as the linked issues touch it.

## Follow-Up Issues

- #85: durable outbox events — outbox enlistment in the unit of work,
  uniform event routing in all hosts, and elimination of the
  fire-and-forget notification sites.
- #86: endpoint-to-use-case migration — the named endpoint violations move
  behind command handlers that own a single commit.
- #87: DbContext concurrency — sequentialize the named parallel read paths
  and document snapshot semantics.
- #88: DAT lineage saga — `DatFile` lifecycle state and the phased,
  resumable replacement saga with retention-aware cleanup.

Guardrail tests accompany each migration slice; there is no separate
omnibus guardrail issue.
