# Library publication

The admin configures an audience; the worker publishes its consumer snapshot.
See [audience libraries](../features/library-audience-workspace.md) for the UI
and [split-host topology](split-host-topology.md) for process ownership.

## Publication is explicit

**Save changes** persists rules. **Refresh library** accepts a materialization
job, including the first publication of a new library. ROM imports and rule
edits mark updates available without automatically scheduling publication.
There is no periodic library refresh. The worker dispatches accepted jobs and
recovers their delivery after a restart.

Pending changes preserve the last published snapshot for consumer reads,
delivery, play history and exports. This includes access-narrowing edits to
ratings, tracking, sources and library rules. Administrators must refresh to
make those restrictions live. A never-published library is unavailable; an
invalid library configuration is unavailable, and refreshing it withdraws its
publication.

Publication does not preserve deleted data. Hard-deleting a title cascades its
snapshot rows away immediately. Moving a release to another title hides it
until the next refresh.

## Pending changes and concurrency

`NeedsMaterialization` marks pending access-narrowing changes. Additive updates
do not clear it. `UpdatedAt` later than `LastMaterializedAt` also identifies
unpublished additive changes; a successful publication sets both together.
Title-scoped metadata reconciliation is separate from library publication.

The active-library index prevents two active materializations for one library.
An execution fence rejects late publication from an older cancelled job, and
the materialization compare token rejects rules changed during computation.
Catalog topology fencing prevents publication across a conflicting rebuild.
A rejected refresh is **Deferred**; refresh again after changes settle.

## Counts and collections

Admin library `itemCount` counts releases. One title can have several regional
versions or revisions. Consumer title and availability counts answer different
questions; do not relabel a release count as a game count.

Collection attachments control audience presentation and ordering. They do not
override library access rules or enqueue materialization. Empty collections
after audience filtering stay manageable in the admin but are omitted from
consumer shelves.

## Configuration previews

Library policies own immutable snapshots of their nested lists. Creation and
configuration updates validate the policy before changing the library, so later
changes to a caller's input cannot bypass revision rotation or refresh state.

Draft comparisons evaluate up to 2,048 titles per batch within one repeatable-read
snapshot. Each title's candidates stay together; retained memory depends on its
release and ROM fanout. Exact saved/draft counts and exclusion summaries still
require a pass over the catalog on each request; only one candidate batch and
the requested page are retained. Paging does not cache a result across catalog generations.
The [preview scale harness](../../benchmarks/Romd.ScaleHarness/README.md) qualifies
batch-size changes against real candidate SQL and independently checked results.
