# Unified artwork completion

## Enrichment workflow

Configure provider connections under **Integrations** and artwork automation
under **Settings**. Open **Enrichment** to review tracked titles, then choose a
view: **Needs review**, **Can enrich automatically**, **Processing**, **Blocked**
or **All tracked titles**. A title can appear in more than one view when its
requirements need different kinds of attention.

Choose **Enrich titles** to start a run. **What to enrich** offers **Metadata
and missing artwork** or **Missing artwork only**. Under **Titles to check**,
choose all tracked titles, titles that can be enriched automatically, or the
current search when that scope is offered. Review the missing-role counts before
starting. A title's own workspace also offers enrichment for that one game.

Select a title to inspect its identity, metadata and artwork requirements. Review
the game, platform, release year and edition before confirming a provider match;
similar names can refer to ports, remakes or modifications. Search again or use
an exact provider game ID or URL when the suggestions are insufficient. Confirming
a match queues durable follow-up work. Wait for it to finish before checking the
resulting cover. Existing artwork and explicit curation choices are preserved.

The **Match**, **Metadata** and **Artwork** editors share the title workspace.
Use [artwork curation](artwork-curation.md) to choose or upload a particular image.
Unsaved edits stay protected when changing titles; a completed save allows a
waiting navigation to continue.

## Follow a run

All-tracked and filtered scopes include matching titles beyond the loaded browser
page. Follow accepted work in **Jobs** or the workspace's recent runs. Results
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
