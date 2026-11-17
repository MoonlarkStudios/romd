# Content ratings and Library enforcement

ROMD retains ratings from multiple boards and applies each Library's policy when
it builds the consumer catalog. Administrators configure that policy in the
[Library workspace](../features/library-audience-workspace.md#choose-games-and-ratings).

## From claim to title rating

1. Providers and administrators supply raw, board-specific claims in the title's
   metadata layers. A claim carries the source code and may include a provider
   rating ID, descriptors, and synopsis.
2. During title rematerialization, ROMD resolves recognized claims through
   [`RatingBoardCatalog`](../../src/Romd.Domain/Catalog/Ratings/RatingBoardCatalog.cs)
   and applies metadata source precedence per board. A user rating can
   replace the effective rating for that board while other boards retain their
   own claims and provenance.
3. The title stores one effective rating per board. Its conservative minimum age
   is the highest age across rated boards, or null when none has an age.

The catalog owns the canonical codes, designations, and age table. A code is
interpreted within its board. Unrecognized claims do not produce an effective
rating. For ACB, the catalog interprets advisory PG and M conservatively as
ages 8 and 15.

## Library policy

[`ContentRatingPolicyEvaluator`](../../src/Romd.Domain/Libraries/ContentRatingPolicy.cs)
returns a verdict, reason, and the basis rating when available.

| Situation | Policy |
| --- | --- |
| Any board refuses classification | Block by default. The Library must explicitly allow refused classification before it can expose the title. |
| Rated categories exist | **Strictest** uses the highest minimum age. **Preferred** uses the first rated category in the configured board order. If a basis is selected, compare its age with the Library ceiling. |
| No rated category supplies a basis | Apply the Library's missing-rating rule. The default flags the title for review and keeps it hidden. Explicitly allowed refused classification can supply an allowed verdict here. |

[`ContentRatingPolicy`](../../src/Romd.Domain/Libraries/ContentRatingPolicy.cs)
defaults to Strictest, an age ceiling of 18, and review for missing ratings.
Manual title inclusion can bypass genre filtering. Rating checks still apply to
manual inclusions and titles shown through attached collections. To correct a
title's rating, edit the title's board claim and let it go through the same
policy evaluation.

[Library publication](library-publication.md) owns how edits, re-enrichment, and
policy changes reach consumers. Published views remain in use until the Library
is refreshed. If underlying provider claims need rebuilding, use IGDB
re-enrichment and refresh the affected Libraries after the title ratings have
been rematerialized.

## Console access

The server's live Library determines online release access. After verified
acquisition, Ottercade keeps authorization scoped to the local profile, server
instance, and release. It checks that exact release before online launch and
can use a previously authorized local row offline under the
[console access rules](../../clients/romd_console/docs/architecture.md#access-and-launch).
An explicit denial revokes the cached authorization. Ottercade delegates rating
evaluation to the server, including when Library access changes.

The [profile access decision](../../clients/romd_console/docs/decisions/profile-access-and-offline-grants.md#optional-pin)
owns the optional parental PIN design. The
[console architecture](../../clients/romd_console/docs/architecture.md) tracks
which parts of that decision are implemented.
