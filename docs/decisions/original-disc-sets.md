# Original Disc Sets

Related decisions: [game media](game-media.md) (multi-file media and CHD),
[media delivery](media-delivery.md), [consumer runtime boundary](consumer-runtime-boundary.md),
[console profile access and offline grants](../../clients/romd_console/docs/decisions/profile-access-and-offline-grants.md).
Current server contracts live in [disc-set architecture](../architecture/disc-sets.md);
[console installation](../../clients/romd_console/docs/original-disc-sets.md)
owns acquisition, repair and local snapshot identity.

## Context

Many games ship on several discs, and players expect to install and play the
set as one game. Each disc is still its own release with its own metadata,
access checks and delivery, and the Library's policy applies to every member.
DATs often list only BIN tracks, while emulators need the original CUE
descriptor that ties tracks together. Uploaded descriptors vary in quality,
and filenames are no proof of which disc a descriptor describes.

Sets arrive incomplete: a total may be unknown, or an expected disc missing.
Grouping, descriptors and authorization have to stay truthful in each of these
cases, without inventing data to make delivery work.

## Decisions

### Select A Set As A Library Unit

- **Members travel together.** Selecting a disc set includes its ordered
  member discs together, subject to the same Library's policy for every
  member. Individual releases keep their ids, metadata, access checks and
  individual delivery paths.
- **A set grants nothing.** A set neither creates ownership nor overrides
  exclusions, ratings, system or source scope, or publication. No member
  borrows authorization from another Library, and selecting a set never
  replaces individual member eligibility with a broader implicit rule.
- **Grouping is reviewed apart from publication.** An administrator accepts
  or corrects grouping separately from publishing Library changes.
- **One install action.** The client presents one set installation action and
  keeps access to individual discs.
- **Editions stay distinct.** Edition, region, language and revision remain
  distinct, and set selection never joins editions by cleaned title.
- **Independent of storage.** Disc grouping is independent of compression and
  CHD representation.

### Preserve And Explicitly Associate Original Descriptors

- **Original bytes are kept.** ROMD keeps uploaded original CUE bytes and their
  required companions.
- **Explicit association.** A descriptor is associated with an exact,
  catalog-matched disc content identity through explicit, validated file
  bindings. Admins supply or replace a missing descriptor through a repair
  workflow. Similar filenames alone never establish the association.
- **Preservation follows identity.** A DAT track match alone does not establish
  an original CUE. For compressed-only input, the kind's adapter regenerates a
  descriptor from measured track metadata and DAT paths. If its exact bytes
  match the DAT descriptor hash, it is an original; otherwise it is a derived
  delivery file with its own hash and never establishes preservation. ROMD
  never invents a DAT requirement, rewrites uploaded originals or substitutes a
  guessed layout for measured facts. Provenance distinguishes uploaded,
  DAT-verified regenerated and derived descriptors.
- **Separate identity and revision.** Descriptor and companion attachments keep
  their own identity and revision, separate from catalog release identity.
  Every dependency binds exact bytes, length and a portable relative path.
- **Pinned layouts stay immutable.** Reconstruction manifests are typed and
  immutable, and own exact descriptor bytes and dependencies. A later uploaded
  original can replace a derived delivery choice through explicit publication;
  existing grants and installed snapshots never silently change.
- **Attachments don't open admission.** Extra files outside the DAT need
  explicit attachment roles and policy, and allowing a CUE never enables
  general admission of unidentified ROMs.
- **Bindings follow content.** Catalog refresh keeps a valid binding to
  unchanged content and surfaces a stale or unresolved binding when content
  identity changes. Repair is explicit, audited and safe against concurrent
  edits, and it never silently replaces a pinned layout used by an existing
  delivery or install.
- **Supported scope.** Supported descriptors are one original UTF-8 CUE with
  its BIN and audio dependencies per disc. Single ISO and IMG images remain
  supported. Other descriptor families and protected-disc qualification are
  unsupported.

### Explain Unknown And Incomplete Sets Separately

- **Unknown stays unknown.** An unknown total remains unknown, even when the
  observed ordinals are contiguous: for example, "2 known discs; total
  unknown." Known discs can be installed when they are authorized and
  individually deliverable. ROMD never claims from this that the game is
  complete or playable.
- **Missing discs need an explicit choice.** When an expected disc is absent,
  or a known member lacks deliverable media, the client shows the missing
  status and requires an explicit partial-install choice. The user confirms
  the exact selected discs.
- **No silent omission.** A member is never silently omitted or renumbered. A
  partial install, even of a single complete disc, is recorded as a partial set
  install, never as a full set.
- **Three separate axes.** Membership expectation, per-disc delivery readiness
  and Library permission are separate. Unusual roles, contradictory totals and
  unresolved membership remain admin review items.
- **Partial never overrides.** A partial installation cannot override a denied
  member, make a broken CUE usable, or disclose restricted releases. Every
  selected disc includes its complete descriptor dependency closure.
- **Updates are offered, not applied.** A later repair or a newly discovered
  disc offers an explicit install update. It never mutates an installed
  snapshot, silently expands a manual set correction, or changes a playlist
  during play.

### Ownership Boundaries

- **The server owns** grouping, Library authorization, descriptor bindings,
  selected ordered delivery snapshots, and deterministic derived M3U bytes and
  hashes.
- **Clients own** local placement, verification, installation, repair and
  resume, and emulator adaptation. They consume structured group data rather
  than inferring membership from filenames.
- **Sources are not qualification.** Accepting a set's sources never
  establishes emulator qualification. Group playback is enabled in production
  only after real disc-switch and shared-save qualification.
- **Group launch has its own boundary.** Single-release authorization and
  offline behavior follow
  [profile access](../../clients/romd_console/docs/decisions/profile-access-and-offline-grants.md). Group launch
  needs an equivalent boundary bound to the installed group snapshot. A
  release grant or short-lived download URL never stands in for it.

## Trade-offs

- **Review before sets work.** Grouping needs an administrator's acceptance
  before a set behaves as one unit.
- **Incomplete sets stay incomplete.** Players see unknown totals and missing
  discs rather than a guessed complete set.
- **Descriptor identity can remain unknown.** A regenerated CUE without an
  exact DAT hash match is deliverable as derived content, while preservation
  remains incomplete until the original arrives.
- **Narrow descriptor support.** Only UTF-8 CUE with BIN and audio, and single
  ISO or IMG images, are supported.

## Rejected Alternatives

- **Joining editions by cleaned title.** Rejected: regions, languages and
  revisions are different releases, and merging them misstates both catalog
  and authorization.
- **Treating every generated descriptor as original.** Rejected: track matches
  do not prove original descriptor text. Only an exact descriptor hash match
  establishes preservation; other generated bytes are explicitly derived.
- **Rejecting all regenerated descriptors.** Rejected: compressed-only media
  must be installable, and an exact DAT hash match establishes original identity
  independently of how those bytes were produced.
- **Inferring membership or descriptor association from filenames.**
  Rejected: similar names prove nothing about content identity.
- **Letting a set carry authorization for its members.** Rejected: each member
  stays subject to its own Library policy.
- **Installing the known discs of an incomplete set silently.** Rejected: the
  user must see what is missing and choose a partial install explicitly.

## Guardrails

- **`DiscSetTests` and `DiscSetProposalBuilderTests`:**
  - unknown totals are never inferred
  - regions, revisions and sources never merge by cleaned title
  - conflicting totals, duplicate ordinals and unusual roles require
    correction
- **`OriginalCueDescriptorTests`:** descriptors keep their exact authored
  references and bytes, and unsafe or self-referencing paths are rejected.
- **`OriginalDiscDescriptorIntakeTests`:**
  - original bytes are preserved
  - malformed CUEs stay visible for repair
  - publication rolls back with the file ledger on failure
- **`CheckConsumerDiscSetAccessTests` and `IssueConsumerDiscSetManifestTests`:**
  - access revalidates the exact snapshot
  - an unavailable Library conflicts rather than revokes
  - changed published revisions revoke
- **`DiscSetDeliveryEndpointsTests` and
  `ConsumerOriginalLayoutGrantServiceTests`:** delivery of the selected ordered
  snapshot and its layout grants.
