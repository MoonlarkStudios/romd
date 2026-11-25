# Unified artwork completion

## Enrichment workflow

Configure provider connections under **Integrations** and artwork automation
under **Settings**. Open **Catalog → Needs review** to review tracked titles.
Use the queue state filter to see automatic work, processing titles, blockers or
all tracked titles. A title can appear in more than one state when its
requirements need different kinds of attention.

Choose **Enrich titles** to start a run. **What to enrich** offers **Metadata
and missing artwork** or **Missing artwork only**. Under **Titles to check**,
choose all tracked titles, titles that can be enriched automatically, or the
current search when that scope is offered. Review the missing-role counts before
starting. A title's own workspace also offers enrichment for that one game.

Choose **Start review** to open the first title in the current queue, or choose
**Review** beside a specific title. The match review shows the catalog game and
the provider's candidates together. When the provider supplies dates for your
system, the candidate shows those dates alongside its first release year.
Use **Find another match** to search the provider or look up an exact game ID or
URL, then **Show original suggestions** to return to the original candidates.

Confirming a match queues follow-up enrichment. Continue with **Next decision**
or **Next title**, or choose **View job** to follow the run. **Skip for now**
leaves the decision open. Use **Continue without IGDB** or the equivalent
provider action to turn off automatic matching for this title. You can link
the provider later from the Title page.

Navigation pauses while a provider decision is saving. If the decision changes
while you are editing, your selection stays visible. Choose **Show latest
status** to clear the draft and review the updated decision. **Open in title**
goes to the relevant Title section, and Back restores your queue search and
selected title.

On desktop, use Up and Down from a review row to move through titles and Enter
to enter the decision. Escape closes the panel. On narrow screens the decision
uses the full workspace, with **Back to review queue** above it.

The Title page's enrichment status includes **Change match**. Use **Metadata**
to edit fields and choose sources, and [Artwork](artwork-curation.md) to compare,
choose or upload images. Unsaved edits require a discard decision when you leave.

## Follow a run

All-tracked and filtered scopes include matching titles beyond the loaded browser
page. Open the job in **Jobs** to follow accepted work. Results
show individual title/role outcomes; a title can still need a match decision while
another requirement is processing. Cancellation leaves already published work
intact. Retry unresolved titles when the underlying problem is fixed.

Confirming a provider match queues follow-up work. When a provider is throttled,
the job can wait for its retry time without losing completed results. A provider
failure is different from a completed search that found no suitable image.

## Review identities and omissions

Check the game, edition, system and year before accepting a match. Limited or
ambiguous suggestions need review; search again or use an exact provider game ID
or URL. A ranking score is not a probability that the match is correct.
A verified provider bridge can establish a SteamGridDB identity automatically.
An edition-preserving name, an unambiguous active DAT hash match, and a
corroborating IGDB release year on the title's platform can support a
provisional SteamGridDB match. ROMD can fill missing artwork from that match
using the configured providers and review settings for each role. The provider
row stays in review. Confirm the game to keep its artwork, choose another
game, or use **Reject provisional match** to withdraw its contributions.
A change in the supporting evidence also withdraws provisional contributions.

An exact IGDB name and platform release can fill metadata gaps provisionally
when an active DAT hash match identifies one release family. The title details,
catalog filters, and rating summaries check that the association is still
current. Existing published fields and ratings have priority. Library access
rules use published metadata. Confirm the game to promote its metadata and
recompute affected library rules.

Open **Review match** to compare the local title with the proposed game and
inspect its evidence, fields, and retained provisional artwork. Use **Find a
replacement** to search or resolve a provider ID. **Back to suggestion** returns
to the proposed game. The review explains what confirmation, replacement, and
rejection do. If the evidence changes as you work, **Reload latest** shows the
current state before you decide again. The expired review shows that ROMD
withdrew its provisional contributions.

Replacing or unlinking a provider match withdraws that match's metadata and
automatic artwork. ROMD recomputes the title using the remaining sources.
User metadata, gallery copies and pinned artwork remain available. A replacement
queues new enrichment. Unlinking suppresses automatic matching for that provider.

You can deliberately leave an artwork role empty or accept a missing metadata
field when appropriate. These choices are retained and can be reversed later.
Existing pins, hidden candidates and confirmed identities remain authoritative.
A no-candidate result for an optional backdrop or logo can use the presentation
fallback without becoming a permanent explicit omission.

For developers: [artwork architecture](../architecture/artwork-pipeline.md)
covers durable jobs, provider identity, acquisition budgets and publication.
