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
leaves the decision open. **Open in title** goes to the relevant Title section,
and Back restores your queue search and selected title.

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
SteamGridDB suggestions require confirmation unless an independently trusted
provider bridge establishes the identity.

You can deliberately leave an artwork role empty or accept a missing metadata
field when appropriate. These choices are retained and can be reversed later.
Existing pins, hidden candidates and confirmed identities remain authoritative.
A no-candidate result for an optional backdrop or logo can use the presentation
fallback without becoming a permanent explicit omission.

For developers: [artwork architecture](../architecture/artwork-pipeline.md)
covers durable jobs, provider identity, acquisition budgets and publication.
