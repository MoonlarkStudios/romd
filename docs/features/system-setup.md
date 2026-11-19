# Your systems and catalog sources

A system is a local collection choice. A catalog source contributes definitions to
that system; its DAT is an installed document version. A subscription is one way
to update a source. Multiple manual and subscribed sources can coexist.

**Systems** shows the installed catalog and source update status separately.
**Known titles** matches the system's **All titles** view. **DATs** counts installed
documents, one current version per source, including disabled sources. **Last DAT
import** is the newest import date among those versions.
Use **Needs attention** to find systems that need a first DAT, catalog work, or
an update review or retry. On wide layouts, select a system to see its catalog
and next action beside the list. On narrower layouts, open the system directly.
Returning to Systems restores the list filters and selection.

Opening a system shows **Metadata** first. Administrators can edit the display
name and description. In **Aliases**, type a name after the badges and press
Enter. New aliases appear alongside saved ones. Choose **Save changes** to keep
new aliases. The remove control on a saved local alias removes it immediately.
Built-in aliases and aliases of unknown ownership are locked.

Custom systems also allow edits to manufacturers and hardware facts. The system
key stays fixed. An alias belongs to one system, with case and repeated spaces
ignored.

Known systems start with a paragraph from the ROMD catalog. Edit it or use
**Clear description**, then choose **Save changes**. Clearing it keeps it
blank across catalog updates. **Restore default** returns the description to
ROMD's current wording while keeping edits to other fields.

**Add system** searches names and aliases on the server. Choose a system and its
first DAT in the same flow. For a known system, review its details and make
any edits before continuing to DAT options. ROMD uses the fixed system key to match
verified subscriptions. Built-in manufacturers, category, generation, release year,
and existing aliases are shown with the system details. Built-in hardware facts
are fixed. You can add name aliases for matching DAT headers. If an alias is
already registered, ROMD shows which system uses it. Case and repeated spaces
do not distinguish aliases.

Choose **Custom system** for a system outside the reference catalog. Custom
systems get a suggested system key with a fixed `local-` prefix. You can edit
the part after the prefix before creation. You can select
registered manufacturers, enter optional hardware facts, and add aliases.
They use manual DATs. Release year means the first public hardware release.
Unknown facts are left blank. Release histories include
[Sony Interactive Entertainment](https://sonyinteractive.com/en/our-company/expanded-company-timeline/),
[Nintendo](https://www.nintendo.co.jp/corporate/en/history/index.html),
[Sega](https://www.sega.co.jp/en/company/history/), and the
[Atari collection at The Strong](https://museumofplay.org/app/uploads/2021/09/Finding-Aid-to-the-Cort-and-Barbara-Allen-Atari-packaging-design-collection_053018.pdf).

ROMD offers verified subscriptions from the signed
directory, with a choice when formats differ. You can also **Upload a DAT** or
choose **Add without a DAT** and return to **Catalog sources** later. A candidate
waits for review before import. If you close setup after a subscription is
created, continue from its row under **Catalog sources**. Restoring a system
keeps its sources and subscriptions. Shared reference updates add picker choices
without enabling them.

**Catalog sources** shows the installed version, DAT entry count, source status,
and update method. Entry counts describe a source and may overlap across sources.
Rows with updates to review, checks to retry, or imports in progress appear first.
Pending subscriptions appear before their first document is installed.

Choose **Add first DAT** or **Add source** to pick a verified subscription or
upload a DAT. For a compatible installed source, choose **Connect subscription**
on its row. ROMD binds the chosen source explicitly. To replace a manual DAT,
choose **Upload new version** on its row or open that action from its menu.
Managers can replace manual DATs. Administrators manage subscriptions and add
new sources. If the publisher directory is unavailable, manual replacements and
saved subscription reviews remain available through their existing validation.

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
through **Connect subscription**. The worker checks subscribed sources daily and
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

For contributors, source lifecycle mutations require Manager permissions and a
current impact token binding the reviewed source versions and title outcomes.
Identity retention is separate from current availability. See the
[database boundary](../self-hosting/configuration.md#database-and-installation-boundary)
for the supported installation schema and [host ownership](../architecture/split-host-topology.md)
for worker-first provisioning.
