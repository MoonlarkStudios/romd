# Parental Controls — Content Rating Data Model & Architecture

Status: **Server implementation updated through cleanup.** Domain/persistence,
provider-backed multi-board ratings, Library policy enforcement, and legacy
content-rating cleanup are implemented. Remaining work includes richer
read-surface UX, optional per-user tightening, and the console-side
server-instance/profile-local-game launch boundary. Its backend identity,
live-Library single-release access, and manifest binding are implemented; local
Console persistence, acquisition, launch enforcement, UX, and PIN remain planned
in `docs/decisions/console-profile-access-and-offline-grants.md`. Responds to the
design review recommending provider-backed multi-board ratings on Title plus
library/user policy, replacing the single `Title.ContentRating` string.

---

## 1. Goals

1. **Accurate** — preserve each rating board's real category (E10+ ≠ E, PEGI 12 ≠ PEGI 16)
   instead of collapsing into a 4-bucket enum.
2. **Reliable** — one server policy path, fail-closed for unknown, pending, and
   refused-classification content. Console offline play may honor a previously
   authoritative allow until the console learns an explicit revocation; network
   failure is never interpreted as either allow-without-history or revocation.
3. **Principled** — canonical board knowledge lives in domain code in exactly one
   place; provider raw data is preserved verbatim for provenance and re-derivation;
   policy interpretation is a pure, testable function.

Non-goal: Collections gain no access semantics. They remain curated groupings;
access control is enforced by Library policy only (per the design review).

## 2. Baseline Implementation Review

This section records the pre-Phase 1 baseline. The enforcement *architecture* was
already right; the *data model* underneath it was too lossy and had one real
bypass bug.

Implementation status: F1-F8 are addressed in the active code path. The legacy
scalar, four-bucket enum, parser, backfill endpoint, and compatibility
conversions have been removed. Manual full rematerialization plus IGDB
re-enrichment is the supported rebuild path after these changes.

What's good and should be kept:

- **Single enforcement point.** Library materialization
  (`MaterializedLibraryProjectionBuilder`, `src/Romd.Domain/Libraries/MaterializedLibraryProjection.cs`)
  is the only place rating filtering happens. Consumer browse and manifest
  delivery (`IssueConsumerReleaseManifest`) read only materialized rows scoped by
  `LibraryId` — consumers never recompute exposure. Parental controls inherit this
  for free.
- **Fail-closed bones exist.** `UnknownRatingPolicy` defaults to `NeedsReview`,
  and `LibraryConfiguration.InvalidFailClosedSentinel` exists for broken configs.

Findings:

| # | Finding | Where |
|---|---------|-------|
| F1 | `Title.ContentRating` is a single free-text string resolved through the metadata cascade; one board's label wins, all others are discarded. | `Title.cs:81,539`, `TitleMetadataPayload.cs:17` |
| F2 | `ContentRatingLevelValue` is derived **in the persistence mapper**, not the domain (`ContentRatingParser.Parse` inside `TitleEntity.FromDomain`). Infrastructure owns a domain derivation. | `TitleEntity.cs:206` |
| F3 | `ContentRatingLevel` (4 buckets) is too coarse: E10+ → Everyone; PEGI 3/7 → Everyone; PEGI 12/16 → Teen; USK 12/16 → Teen. Unusable for age-based parental controls. | `ContentRatingLevel.cs`, `ContentRatingParser.cs` |
| F4 | `ContentRatingParser` has cross-board token collisions: bare `"M"` matches ESRB Mature but is an unrestricted advisory category in ACB; bare `"A"/"B"/"C"/"D"/"Z"`, `"G"/"PG"`, and numerics `"10"/"12"/"16"/"18"` are interpreted board-agnostically (ClassInd 16 falls through to the PEGI branch). GRAC is absent entirely; ACB RC (Refused Classification) is unhandled. | `ContentRatingParser.cs` |
| F5 | IGDB enrichment reads **deprecated** `age_ratings.category` / `age_ratings.rating`, takes only the **first ESRB** entry, and discards PEGI/CERO/USK/GRAC/ClassInd/ACB, content descriptors, and synopsis. JP/EU-only titles get `null` → permanent NeedsReview. IGDB docs: use `organization` and `rating_category` instead. | `IgdbMetadataProvider.cs:577-590,660` |
| F6 | **Bypass bug:** `IncludeTitleIds` short-circuits the rating gate — a manually included title skips `MaxContentRatingLevel` and `UnknownRatingPolicy` checks entirely. A misfiled include defeats parental controls. | `MaterializedLibraryProjection.cs:123-124` |
| F7 | `ICurrentUser.MaxContentRating` returns `AdultsOnly` when library config can't be loaded — fail-open at the identity abstraction. | `CurrentUser.cs:96-109` |
| F8 | `ContentRatingParser.ParseOrDefault` defaults unknown ratings to `AdultsOnly` — "unknown" and "most restricted rating" are conflated; policy can no longer distinguish them. | `ContentRatingParser.cs:113` |

## 3. Target Domain Model

### 3.1 Layered flow

Ratings follow the existing three-stage metadata pattern, generalized from scalar
fields to a per-board collection:

```
provider/user claims (raw, per layer)          ← stored verbatim in layer payload
        │  RatingBoardCatalog.TryResolve (canonical domain table)
        ▼
materialized per-board ratings on Title        ← one row per board, source cascade applied
        │  ContentRatingPolicy.Evaluate (pure function)
        ▼
library projection verdict                     ← Allowed / Blocked(reason) / NeedsReview
```

### 3.2 Claims (what providers and users assert)

Added to `TitleMetadataPayload` (claims travel through the existing layer system,
so provenance, user-wins, and per-field overrides keep working):

```csharp
public sealed record ContentRatingClaim
{
    public required RatingBoard Board { get; init; }
    public required string RawCode { get; init; }       // provider's literal label, e.g. "E10", "PEGI Twelve"
    public string? ExternalRatingId { get; init; }      // provider row id, for idempotent refresh
    public IReadOnlyList<string> Descriptors { get; init; } = [];  // "Fantasy Violence", ...
    public string? Synopsis { get; init; }
}

// TitleMetadataPayload gains:
public IReadOnlyList<ContentRatingClaim>? ContentRatings { get; init; }
```

Claims are **raw**: providers never compute ages or normalized levels. If a
provider emits an organization or code the domain doesn't recognize, the claim is
dropped and logged — never guessed (this is what kills F4's token collisions: the
board is always explicit, so `"M"` under `Acb` and `"M"` under `Esrb` are different
facts).

### 3.3 Canonical board knowledge (single source of truth)

One static domain class replaces `ContentRatingParser` + `ContentRatingLevel`:

```csharp
public enum RatingBoard { Esrb, Pegi, Cero, Usk, Grac, ClassInd, Acb }

public enum RatingDesignation
{
    Rated,                  // normal category with a minimum age
    RatingPending,          // ESRB RP, GRAC TESTING — treated as Unknown by policy
    RefusedClassification   // ACB RC — treated as maximally restricted by policy
}

public sealed record ContentRating
{
    public required RatingBoard Board { get; init; }
    public required string Code { get; init; }          // canonical display code: "E10+", "PEGI 12", "CERO B"
    public required RatingDesignation Designation { get; init; }
    public int? MinimumAge { get; init; }               // null iff not Rated
    public IReadOnlyList<string> Descriptors { get; init; } = [];
    public string? Synopsis { get; init; }
    public required string SourceId { get; init; }      // "igdb" | "user" | future provider
    public string? ExternalRatingId { get; init; }
}

public static class RatingBoardCatalog
{
    // (Board, normalized code) → (canonical code, designation, minimum age).
    // Closed table, exhaustively unit-tested. Unrecognized input → null, never a guess.
    public static ContentRatingCategory? TryResolve(RatingBoard board, string rawCode);
}
```

Canonical age table (the product decision, recorded once, tested exhaustively):

| Board | Categories → MinimumAge |
|-------|------------------------|
| ESRB | EC→3, E→0, E10+→10, T→13, M→17, AO→18, RP→pending |
| PEGI | 3, 7, 12, 16, 18 |
| CERO | A→0, B→12, C→15, D→17, Z→18 |
| USK | 0, 6, 12, 16, 18 |
| GRAC | ALL→0, 12→12, 15→15, 18→18, TESTING→pending |
| ClassInd | L→0, 10, 12, 14, 16, 18 |
| ACB | G→0, PG→8, M→15, MA15+→15, R18+→18, RC→refused |

Notes: ACB PG/M are advisory (unrestricted) categories; assigning 8/15 is a
deliberate conservative interpretation, documented here, encoded in the table, and
trivially adjustable in one place. `MinimumAge` semantics: "youngest age the board
deems the content suitable for, conservative reading."

### 3.4 Materialization onto Title

`Title.Rematerialize` resolves ratings **per board** with the existing cascade
(field override → user layer → platform default → global priority): for each
board, the highest-priority layer that asserts a claim for that board wins. The
result is a child collection on Title — one effective `ContentRating` per board —
persisted to a queryable table:

```
TitleContentRatings
  TitleId           int       FK → Titles
  Board             int       (RatingBoard)
  Code              text
  Designation       int
  MinimumAge        int?
  DescriptorsJson   text
  Synopsis          text?
  SourceId          text      -- provenance, mirrors _fieldProvenance
  ExternalRatingId  text?
  UNIQUE (TitleId, Board)
```

Plus one denormalized, indexed scalar on `Titles` for admin catalog filtering and
the inbox lens (replaces `ContentRatingLevelValue`):

- `ConservativeMinimumAge int?` — `max(MinimumAge)` across rated boards;
  `null` when no board has a `Rated` rating. Computed in **domain** during
  `Rematerialize` (fixes F2), not in the persistence mapper.

`Title.ContentRating` (string) and `ContentRatingLevelValue` are dropped in the
cleanup migrations (§7); active contracts expose per-board ratings only.

## 4. Policy Model

### 4.1 Shape

Policy lives on `LibraryConfiguration` (cohesive record replacing the two loose
fields), because Library is already the materialized enforcement boundary that
browse and delivery flow through:

```csharp
public enum RatingBasisSelection
{
    Strictest = 0,   // max MinimumAge across all rated boards — default, safest
    Preferred = 1    // first board in BoardPreference that has a rating
}

public sealed record ContentRatingPolicy
{
    public RatingBasisSelection BasisSelection { get; init; } = RatingBasisSelection.Strictest;
    public IReadOnlyList<RatingBoard> BoardPreference { get; init; } =
        [RatingBoard.Esrb, RatingBoard.Pegi, RatingBoard.Cero, RatingBoard.Usk,
         RatingBoard.Grac, RatingBoard.ClassInd, RatingBoard.Acb];
    public int? MaxMinimumAge { get; init; }            // null = no age ceiling
    public bool AllowRefusedClassification { get; init; } = false;
    public UnknownMetadataPolicy UnknownRatingPolicy { get; init; } = UnknownMetadataPolicy.NeedsReview;
}
```

`MaxMinimumAge` is a plain integer ceiling — a single, board-independent knob the
UI can render as an age slider with board badges ("13 → up to ESRB T / PEGI 12 /
CERO B / USK 12"). Refused Classification is deliberately modeled as a separate
explicit opt-in because it is not an age category.

### 4.2 Evaluation (pure domain function)

```csharp
public sealed record RatingVerdict(
    RatingOutcome Outcome,            // Allowed | Blocked | NeedsReview
    string Reason,                    // machine-readable block reason
    ContentRating? Basis);            // the rating the decision was based on — explainability

public static class ContentRatingPolicyEvaluator
{
    public static RatingVerdict Evaluate(ContentRatingPolicy policy, IReadOnlyList<ContentRating> ratings);
}
```

Rules, in order:

1. **Refused Classification** (any board): `Blocked("RefusedClassification")`
   unless `AllowRefusedClassification` is explicitly true. The default and
   fail-closed sentinel both block RC content, even when `MaxMinimumAge` is null.
2. **Basis resolution**: per `BasisSelection`, over boards with
   `Designation == Rated` only.
3. **Ceiling**: basis found → allowed iff `Basis.MinimumAge <= MaxMinimumAge`
   (or policy unrestricted). Otherwise `Blocked("ContentRating")` carrying the
   basis for explainability ("Blocked: PEGI 16 exceeds max age 12").
4. **Unknown** (no rated boards — none, pending-only): apply
   `UnknownRatingPolicy`. `Allow` is an explicit opt-in, never a default; the
   fail-closed sentinel uses `Hide`.

Invariant (property-tested): **monotonicity** — lowering `MaxMinimumAge` or
switching `Preferred → Strictest` never makes a previously hidden title visible.

### 4.3 Include lists must not bypass the rating gate (F6 fix)

`GetTitleBlockReason` is restructured into two classes of filters:

- **Curation filters** (selection mode, genre): `IncludeTitleIds` may bypass —
  manual curation legitimately overrides curation rules.
- **Safety filters** (content rating): evaluated **unconditionally**, including
  for manually included titles.

The legitimate "parent vouches for this game" escape hatch is not a list bypass —
it is a **user rating claim** (§3.2): the admin re-rates the title via the user
layer ("user: ESRB E"), which wins the cascade, flows through the same policy
evaluation, and leaves provenance/audit ("rated by user") instead of a silent hole.
One enforcement path, no exceptions.

### 4.4 Per-user tightening (phase 5, optional)

Materialized title rows gain the **resolved** basis for their library's policy
(`ResolvedMinimumAge int?`, `ResolvedBoard`, `ResolvedCode`). A consumer user may
then carry an optional `MaxMinimumAge` that can only **further restrict** —
applied as a cheap `WHERE` at the consumer read layer:
`ResolvedMinimumAge IS NOT NULL AND ResolvedMinimumAge <= @userMax`. `NULL`
resolved age fails closed under a user ceiling. No per-user materialization
needed; restriction stays monotonic by construction. Until then, "one library per
child profile" remains the supported model.

### 4.5 Fail-closed identity (F7/F8 fix)

- `ICurrentUser.MaxContentRating` is removed (nothing legitimately consumes a
  fail-open default). Consumer handlers require an ordinary authenticated OAuth
  subject, then resolve the user's live `Users.LibraryId` and current valid
  materialized Library in the shared snapshot boundary. No live Library remains
  "no catalog."
- `ContentRatingParser.ParseOrDefault` is deleted with the parser. Unknown is
  unknown; only policy decides what unknown means.

## 5. Enrichment — IGDB Changes

Replace deprecated fields in all three query sites (multi-query, search,
fetch-by-id):

```
age_ratings.organization.name,
age_ratings.rating_category.rating,
age_ratings.rating_content_descriptions.description,
age_ratings.synopsis
```

Mapping in the provider (Infrastructure):

- `organization.name` → `RatingBoard` via a closed name map (`"ESRB"`, `"PEGI"`,
  `"CERO"`, `"USK"`, `"GRAC"`, `"CLASS_IND"`/`"ClassInd"`, `"ACB"`). Unknown
  organization → claim dropped + warning log (forward-compatible with IGDB adding
  boards).
- `rating_category.rating` → kept verbatim as `RawCode`; canonicalization happens
  in `RatingBoardCatalog` at rematerialization, so a provider data quirk never
  corrupts stored claims.
- **All** age ratings are persisted as claims — no first-match `break`, no
  ESRB-only filter. `EnrichmentData` gains `ContentRatings` and drops the scalar
  `ContentRating` scalar is gone after cleanup.

Backfill: existing `BulkEnrichmentJob` infrastructure re-enriches per platform to
populate claims for the full catalog.

## 6. Read Surfaces

- **Admin Title detail**: ratings panel shows all boards with code, age,
  descriptors, synopsis, and per-board provenance (source badge, consistent with
  the metadata cascade inspector). User re-rate action writes a user-layer claim.
- **Admin "Needs review" lens**: titles whose verdict is `NeedsReview` for a given
  library are derived by running `ContentRatingPolicyEvaluator` over candidates —
  pure function, no new state table. Fits the existing Inbox pattern.
- **Library editor**: age slider + board badges, basis selection
  (Strictest/Preferred + board order), unknown-rating policy with `NeedsReview`
  default and copy that makes `Allow` clearly unsafe.
- **Consumer browse/detail**: display the resolved basis rating
  (`ResolvedBoard/Code`) from materialized rows — never recomputed client-side.
- **Console local surfaces and launch**: the console never recomputes rating
  policy. The server Library is the sole content-policy evaluator. After a
  successful verified download or attach, the console retains a profile- and
  server-instance-bound local game and checks that exact release again before an
  online launch. It has no local content-policy mode or console-local content
  blocks. Server origin is only a locator; stable instance identity prevents a
  replacement server from accidentally inheriting cached games. The public UUID
  is not authentication; configured HTTPS/TLS plus ordinary OAuth authenticates
  the server/user. See
  `docs/decisions/console-profile-access-and-offline-grants.md`.

## 7. Migration Plan

Phased, each phase shippable:

1. **Domain + persistence — implemented**: `RatingBoard`, `ContentRating`,
   `RatingBoardCatalog`, `ContentRatingClaim` on payload, per-board resolution in
   `Rematerialize`, `TitleContentRatings` table, and `ConservativeMinimumAge`
   column. A temporary legacy-string backfill existed during Phase 1; it has now
   been removed.
2. **IGDB provider — implemented** (§5): all IGDB age-rating organizations,
   rating-category values, descriptors, and synopsis are stored as raw claims.
   Platform-wide bulk re-enrichment is the supported data rebuild.
3. **Policy cutover — implemented**: `ContentRatingPolicy` is the active
   `LibraryConfiguration` contract; projection builder consumes `RatingVerdict`;
   **F6 bypass is fixed**. `MaxContentRatingLevel`/`UnknownRatingPolicy`
   compatibility was intentionally removed instead of carried forward.
4. **Cleanup — implemented**: dropped `Title.ContentRating`,
   `ContentRatingLevelValue`, `ContentRatingParser`, `ContentRatingLevel`, and
   the legacy content-rating backfill endpoint/service; admin catalog filters now
   use per-board facets.
5. **Read-surface polish + optional per-user tightening — remaining** (§4.4,
   §6): richer admin ratings panel, needs-review lens, resolved-basis consumer
   display, and per-user ceilings.
6. **Console profile-local games and launch enforcement — in progress**: backend
   stable server identity, live-Library single-release access, and manifest
   instance binding are implemented. Flutter discovery and strict parsing,
   profile/instance rows created after verified acquisition, generous offline
   use of authorized rows,
   explicit launch-time revocation, and one launch gate. See
   `docs/console-profile-home-library-plan.md`.

## 8. Testing Strategy

- **`RatingBoardCatalog`**: table-driven test covering every (board, code) pair,
  including pending/RC designations and rejection of unknown codes. This table is
  the safety-critical artifact; it gets exhaustive coverage.
- **Policy evaluator**: truth-table tests (rated/pending/RC/empty ×
  Strictest/Preferred × ceiling above/at/below × unknown policies);
  property test for monotonicity (§4.2).
- **Projection**: regression test proving `IncludeTitleIds` cannot bypass the
  rating gate; existing invariant assertions extended with
  "no materialized title in a ceiling-limited library exceeds the ceiling."
- **IGDB provider**: fixtures with the new expanded payload — multi-board,
  unknown organization, RC, pending, descriptor arrays.
- **Cleanup/migration**: EF migration coverage for dropping legacy title columns
  and recreating FTS; contract/client tests proving the scalar fields no longer
  exist; policy tests proving legacy include lists do not bypass safety filters.
- **Console enforcement**: integration tests proving two consumers with
  different Libraries receive different single-release decisions; console tests
  for authorized/unauthorized rows, no row, malformed or wrong-instance response,
  same-token live Library reassignment, verified acquisition, server replacement,
  and direct-launch bypass attempts.

## 9. Decisions Recorded

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Where ratings live | Claims in metadata layers → materialized per-board rows on Title | Reuses provenance/cascade machinery; no parallel metadata system |
| Cross-board comparison | Canonical per-board `MinimumAge` table in domain | Single integer policy knob; board nuances preserved in stored data |
| Default basis | `Strictest` across boards | Parental controls are the stated center of the feature; fail-conservative |
| Unknown content | `NeedsReview` default; `Allow` explicit opt-in | Fail closed (per design review) |
| RC (ACB) | Blocked under any ceiling | "Refused classification" is not an age |
| Include-list vs rating gate | Rating gate is unconditional; exception = user re-rate via user layer | No silent bypass; auditable provenance |
| Policy home | `LibraryConfiguration` (cohesive `ContentRatingPolicy` record) | Library is the existing materialized enforcement boundary through browse + delivery |
| Collections | No access semantics | Per design review |
| Console offline access | An authorized profile local game created only after verified download/attach has no default expiry | Preserves generous offline play without treating outages as revocation |
| Console install relationship | Play requires an exact server-instance install plus that profile/instance/release row | Shared storage and an honest replacement presenting a different instance id cannot bypass Library policy or create a row; copied-id impersonation is outside this namespace-only boundary |
