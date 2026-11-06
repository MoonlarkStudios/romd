# Admin Use-Case, Transaction, And Domain-Event Boundaries

Related decisions: [API contract and compatibility policy](api-contract-policy.md),
[host plane ownership](plane-ownership.md)

## Context

Admin mutations arrive from HTTP requests and from Hangfire workflows, and
both must behave the same way. A mutation usually has consequences beyond
its own rows: library invalidation, realtime events for first-party clients,
and follow-up work such as rebuilds or enrichment. A crash, a cancellation
or a host restart between the mutation and those consequences must not
leave a state that nobody can name or repair.

Each request or job resolves one scoped `RomdDbContext`, which is not
thread-safe. Some workflows, such as DAT replacement, run far longer than
any transaction should stay open. Event delivery that depends on which host
ran the code, or on work detached from the request, loses events silently.

The current transaction pattern and its helpers are described in
[backend mutations](../architecture/backend-mutations.md). The public
contract for asynchronous work is
[Async Operations And Jobs](api-contract-policy.md#async-operations-and-jobs).

## Decisions

### Use-Case Ownership: One Command Handler Per User-Observable Mutation

- **One handler per mutation.** Every user-observable mutation is owned by
  exactly one application command handler implementing
  `ICommandHandler<TCommand, TResult>`, in
  `Romd.Admin.Application/{Area}/Commands/{UseCaseName}/`.
- **Endpoints stay thin.** An endpoint binds input, applies authorization
  metadata, maps the contract to a command and the result to a response,
  dispatches to the handler, and translates `ErrorOr` results. It never
  references Infrastructure types.
- Why: the same handler then runs identically from a request or a workflow,
  and its business rules live in one testable place.

### Transaction Ownership: The Command Handler Owns One Commit

- **The handler owns the transaction** through the application-owned
  `IUnitOfWork` port.
- **One commit per mutation.** A single commit makes the primary mutation,
  its dependent flag updates and its event intents durable together.
- **Repositories never commit on their own** on command paths. Staged
  repository methods leave commit ownership with the caller.
- **Follow-up work is recoverable, not chained.** A mutation that needs
  derived work splits into one atomic commit and recoverable post-commit
  work under [Post-Commit Work Dispatch](#post-commit-work-dispatch). A
  chain of commits is never the design.

### Domain Events Are Transactional Outbox Intents

- **The outbox row is the event.** A use case records each required admin
  event as an outbox row in the same transaction as the business mutation,
  through the application-owned `IAdminEventOutbox` port. The relay is the
  only fan-out.
- **No in-process event machinery.** There is no MediatR, generic event bus
  or in-process domain-event dispatcher.
- **Events are typed and versioned.** Each row carries an event type and an
  integer payload schema version, delivered to clients with the event. A
  payload schema change bumps the version under
  [Event Payload Compatibility](api-contract-policy.md#event-payload-compatibility).

### Uniform Event Routing Through The Durable Outbox

- Required admin events take the durable outbox in every host.
- SignalR is a delivery transport used only by the relay. No handler sends
  to SignalR directly.
- Why: event-loss semantics must not depend on which host ran the handler.

### No Detached Fire-And-Forget Work

- No detached task, such as `_ = Task.Run(...)` or a discarded task, captures
  scoped services or carries required work.
- The event is recorded in the commit, and delivery belongs to the relay.

### DbContext Concurrency: Scoped Contexts Are Single-Flight

- **Sequential awaits are the rule.** A scoped `RomdDbContext` runs one
  operation at a time.
- **Parallel reads need separate contexts.** They use explicitly created
  scopes or contexts, a stated consistency contract, and a measurement that
  justifies them.
- **Reads are per-query consistent.** A multi-query read promises no
  cross-query consistency.
- Why: `DbContext` is not thread-safe, and concurrent operations on one
  context fail nondeterministically.

### Long-Running Work: Resumable Phased Jobs

A long-running workflow is a resumable Hangfire job with persisted phase
state:

- an explicit phase persisted at each phase boundary
- idempotent steps between boundaries
- defined recovery semantics at every boundary
- cancellation that converges to a terminal or resumable state
- operator-visible failure

Automatic retries wrap only idempotent steps. A retry never replays a
non-idempotent business mutation.

### DAT Replacement Is The Reference Saga

- **Pending ingest.** A replacement ingests the new DAT as a pending,
  inactive version. Catalog truth is unchanged until activation, and an
  interrupted ingest is safe to resume or discard.
- **Atomic activation.** One commit activates the new version, supersedes
  the prior one, applies superseded-version retention (the newest
  superseded version is kept), invalidates catalog and materialization
  state, and records the required events. A retention failure rolls the
  whole activation back.
- **Convergence backstop.** A recurring sweep re-applies retention
  idempotently to repair crashes and races.
- **Lineage stays truthful.** Each source has at most one active version.
  Retained prior versions stay queryable and are never mistaken for active
  truth, and content blobs are kept while any retained version references
  them.
- Why: replacement is too long for one transaction, so activation is its
  single atomic point.

### Cancellation Semantics

- `CancellationToken` propagates through application and infrastructure
  ports.
- The commit is the atomicity point. Cancellation before it rolls back the
  whole mutation. After it, the mutation and its outbox intents are durable,
  so cancellation or shutdown cannot lose a required event.
- Required post-commit effects live in the outbox, never in a request or job
  lifetime.

### Post-Commit Work Dispatch

- **An enqueue is acceleration.** A Hangfire enqueue after a commit is never
  the only record of pending work.
- **Pending work is durable before dispatch.** Either committed state is the
  durable representation of the work and a recovery path re-derives the
  dispatch from it, or the work intent (a job row, request row or outbox
  intent) is persisted in the same commit before any enqueue. Durable
  metadata requests are the model
  ([durable metadata work](../architecture/backend-mutations.md#durable-metadata-work)).
- **Each path names its mechanism** and proves it with a test.

### Read Models Stay Behind Application Ports

Repositories return domain models or purpose-built application read models.
EF entities and `IQueryable` never cross application ports.

### Time Is An Injected Dependency

Where time affects behavior or tests, code uses `TimeProvider` or the
existing clock abstraction, never inline `DateTime.UtcNow` or
`DateTimeOffset.UtcNow`.

## Trade-offs

- **Every mutation needs a handler,** however small; an endpoint cannot take
  a shortcut.
- **Events arrive after the relay dispatches them,** not inline with the
  request.
- **Sequential reads cost latency** on composite screens until a measurement
  justifies separate scopes.
- **Phased jobs cost code:** explicit phase state, idempotent steps and
  recovery handling at every boundary.

## Rejected Alternatives

- **MediatR, a generic mediator or event framework, an in-process
  domain-event dispatcher, or an event bus.** Rejected: there is no
  demonstrated need, and the transactional outbox row plus the relay covers
  every admin event with fewer moving parts.
- **Direct SignalR sends from handlers.** Rejected: event loss becomes
  host-dependent, and send failures are swallowed.
- **Fire-and-forget delivery as a latency optimization.** Rejected: it
  captures scoped services beyond their lifetime and loses events on
  shutdown, and durable delivery makes it unnecessary.
- **One transaction spanning a whole DAT replacement.** Rejected: ingest is
  long-running, and a transaction across it would hold locks and an
  ever-growing rollback for its whole duration. Activation is the only point
  that must be atomic.
- **Parallel queries on one scoped context as the default.** Rejected:
  `DbContext` is not thread-safe, and the gain is unmeasured.

## Guardrails

- **`RepositoryTransactionOwnershipArchitectureTests`:** preserve frozen
  baselines and record explicit retirements only when migration-debt
  capabilities are removed. Require zero remaining measured migration debt,
  reject resurrected capabilities, and guard exact intentional protocol sites.
  Title-port aggregate writes must be staged.
- **`AdminEventOutboxBoundaryTests`:**
  - no detached notification sites
  - Hosting holds no application-outbox references or notifier ports
  - realtime transport stays SignalR-only, with delivery in the relay
  - the unit of work exposes no EF tracker reset
- **`AdminMutationOutboxAtomicityTests`, on real PostgreSQL:**
  - commit failure, cancellation and disposal failure keep each mutation,
    its flags and its events together
  - the DAT replacement saga's pending ingest, activation and retention are
    atomic, with one active version per source
- **`DbContextSingleFlightBoundaryTests`:** admin query handlers and
  persistence repositories use no `Task.WhenAll`, and its allowlist holds
  only files that still need it.
- **`PersistenceBoundaryArchitectureTests` and `TitlePortArchitectureTests`:**
  EF Core and `RomdDbContext` stay inside `Romd.Persistence`, and title ports
  expose no persistence types.
- **`JobLifecyclePolicyArchitectureTests`:** job-family lifecycle behavior
  stays in registered policies, and API hosts compose none.
- **Review:** endpoint bodies, injected time and retry idempotency are
  checked in review.
