# Your systems and catalog sources

A system is a local collection choice. A catalog source contributes definitions to
that system; its DAT is an installed document version. A subscription is one way
to update a source. Multiple manual and subscribed sources can coexist.

**Add system** searches supported identities and aliases, then immediately adds
or restores the system and opens its home. Adding a source is optional. Shared
reference updates add picker choices without enabling them.

**Catalog sources** lists each installed source's version, lifecycle, entry/file
counts, update method, and actions. Counts describe that source and may overlap;
they are not added together as a unique game total. Pending subscriptions appear
before their first document is installed. Subscription matching uses the active
DAT identity, never the first subscription on a system.

**Add source** opens a side panel (full width on narrow screens) offering verified
subscriptions and manual DAT upload. Existing subscriptions are marked connected.
A compatible manual source is connected from its own management panel so that
source selection is explicit. Manual upload also works from Your systems: inspect
the DAT, suggest a matching system, and require confirmation when ambiguous.

Every manual add is reviewed and hash-bound. Matching names offer an explicit
choice between updating a manual source and adding a separate source; display
names do not select replacement targets. Identical installed document hashes are
rejected before accepting another import. **Upload new version** always targets
the chosen source and uses the existing baseline/candidate hash review path.

**Manage source** loads its entries only when opened. Entry pages use cursors;
large update diffs keep their existing bounded browse/review path. Update reviews
use full-screen layouts on phones and preserve existing documents until approval.
Source list polling reads only local subscription state; it polls more frequently
while an accepted job is applying. It never fetches publisher documents.

Source update health is separate from system availability. An installed usable
catalog with a clean projection remains Ready when a subscription check fails or
another source is being prepared. Actual projection failures and unavailable
sources retain their own attention/processing states.

A subscription can be switched to **Manual updates** after confirmation. Only
subscription tracking and its pending selection are detached; installed sources,
DATs, stored files, and completed jobs remain. The operation uses catalog/source
locks and rejects running imports. A compatible source can be connected again
through **Use subscription**. The worker checks subscribed sources daily and
prepares updates for review; activation always requires approval. A pending review
holds its exact candidate until the administrator approves it or explicitly checks
again. The source shows the last check, next scheduled check, and any failure.
Failed checks preserve the installed catalog and retry after 1, 2, 4, 8, 16, then
24 hours; **Retry check** lets the administrator retry immediately.

System **Settings → Remove from Your systems** changes only visibility. Sources,
ROMs, subscriptions, and settings remain, and accepted jobs may finish. Adding the
system again restores it and resumes automatic checks. Removing a system pauses
future checks; a check already in progress may finish. This is separate from source
lifecycle controls and permanent deletion; it does not remove consumer content.

## Persistence and API scope

`Platforms.IsEnabled` remains the local nullable preference established by
migration 7. True/false are explicit choices; null represents untouched registry
definitions, with compatibility inference for existing local data. Legacy system
actor seed rows do not count as human-created systems. Shared reference updates
never overwrite enablement. Migration 8 adds persisted subscription next-check
times and failure counts; restart the worker first to migrate.

Admin endpoints retain the setup/preview/import contract and add
`DELETE /api/dat-subscriptions/{subscriptionId}` for detaching tracking. Generated
admin clients are regenerated from OpenAPI; consumer contracts are unchanged.
The worker uses its existing recurring-job registrar for a 15-minute sweep of due
subscriptions. No automatic activation or source ordering policy is added.

## Source removal and provenance

**Status → Disable Source** previews the titles still covered elsewhere, titles
with local ROMs losing their definition, other titles retained for personal state,
and catalog-only titles without a remaining definition. It pauses subscription
checks and removes the source's contribution, while keeping its versions and
entry links for restoration. **Re-enable Source** restores that contribution.
Discontinued sources follow the same inactive contribution rules.

**Delete source permanently** is a separate action. It requires the source name
and a current impact review, then removes every version and the subscription in
one transaction. It never deletes stored ROM files or asks a console to delete
saves. Checks/imports in progress block the action; a changed impact requires a
new review. A transaction locks affected titles before rechecking the impact, so
concurrent personal edits cannot be silently removed. Counts are distinct titles
after removal, including titles already
without a definition, rather than the sum of DAT entries.

Owned titles retain their IDs even when untracked, including when deleting a
previously disabled source. Tracked titles and existing personal metadata also
retain their identity. Without a remaining definition the title shows **No active
catalog definition**. Unowned catalog-only titles can leave the active catalog;
whole-source deletion removes orphans with no ownership or personal state.
Other sources' definitions remain effective. Projection and library updates run
through the existing convergence path after the lifecycle transaction commits.

The source explorer searches entry and title names, loads 50 entries at a time,
and links each assigned entry to its title. A title's Sources panel links back to
that source filtered to the title. Local payload and active-source counts are
shown separately. **File details** retains the BIOS filter and file/hash view.

Migration 9 adds `Titles.RetainWithoutCatalog`, an identity-retention marker set
before source withdrawal clears entry/payload relationships. It is separate from
current effective availability and does not restore title IDs deleted by older
versions. Restart the worker first. Admin APIs add `source-impact`,
`source-lifecycle`, and `source-entries` beneath `/api/dats/{datId}`; mutations
require Manager permissions, and impact tokens bind the reviewed source versions
and title outcomes. The older per-version delete API remains distinct.

Console save paths are keyed by profile/platform/title identity and source removal
does not invoke uninstall or profile cleanup. This change verifies server-side
identity and stored-file retention; it is not a new device save/restore acceptance
run. It does not expand retry/resume behavior or publisher infrastructure.
