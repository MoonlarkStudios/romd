# Media Delivery

Related decisions: [game media](game-media.md) (identity, stored formats and
reading), [VFS delivery through WebDAV](vfs-webdav-connections.md),
[API contract and compatibility policy](api-contract-policy.md). Current client
behavior is described in [consumer delivery](../architecture/consumer-delivery.md)
and [downloads and exports](../architecture/downloads-and-exports.md).

## Context

One stored artifact per ROM serves several surfaces: admin downloads and
exports, consumer manifests and grants, and VFS connections. Each surface's
clients read different formats; GameCube and Wii emulators read RVZ directly,
and other tools need the original image. A client that verifies, caches or
keys files by path breaks when a URL's bytes change format, or when two
surfaces describe the same file differently. A format that is still being
prepared must never be advertised as ready.

## Decisions

### Delivery Formats

- Delivery formats retain stable wire enum values, separate from storage
  encodings. A ROM offers original logical bytes and each encoding it actually
  holds, subject to verification, host decode capability and request scope.
  Stored formats stream their admitted artifact bytes without decoding.
  Derived formats enter this set only through verified delivery-cache artifacts;
  a configured kind or recipe alone never promises a format.
- Derived artifacts are cache entries, not preservation copies. A background job
  produces each one from the stored artifact, verifies that it decodes to the
  ROM's logical identity, and only then offers it. Cache entries are budgeted,
  evictable and rebuilt on demand; they are never canonical and never count as
  the ROM's stored artifact.

### Delivery Targets

- Authorization produces a complete delivery target that a shared reader opens:
  ROM (or set), delivery format, filename, SHA-256 and length. A pure
  Application policy plans formats from retained facts, current registered
  policy, host decode capability, verification and authorized request scope.
  Runtime claims are separate from byte admission.
- Delivery planning reports each offered format as available, preparing or
  unsupported, with its observation profile and runtime applicability. While a
  ROM converges, its verified retained representation keeps serving until the
  replacement is independently verified and published. Convergence does not
  add an encoding the ROM does not hold.
- A platform or file extension alone never adds a format. Planning lists an
  encoded format only for a ROM that retains an artifact in that encoding, so a
  cartridge or companion file offers `original` alone.
- One pure resolver determines media kind per DAT entry, filtering observed
  layouts and evidence by the entry's platform. A surface with one platform uses
  it as context; a surface without one aggregates the entry results and claims a
  kind only when they agree. Unclaimed content uses the opaque fallback;
  ambiguous or contradicted evidence makes no runtime claim.
- Native runtime applicability comes only from the uniquely resolved kind's
  descriptor and only for its stored format. Held formats admitted by evidence
  and the resident decoder are not hidden by an ambiguous kind. Encoding
  capability never decides whether retained bytes can be read.
- Planning, VFS and delivery surfaces use the same validated composition and
  resolver. Platform keys, format extensions and recipe rules remain in policy
  registrations and adapter boundaries.
- Metadata describes delivered bytes. `original` uses the logical length and
  SHA-256; every other format uses its artifact's length and SHA-256.
- Advertised checksums, including VFS ownCloud MD5/SHA-1, describe the delivered
  bytes. Unknown checksums are omitted. New RVZ artifacts receive measured,
  immutable artifact MD5 and SHA-1 during staging or encoding. Additional
  checksum facts require measurement of the exact retained bytes. Logical hashes
  are never reused as artifact hashes.
- A URL or path always delivers one format. Its bytes never switch to another
  format. When the artifact behind a format is replaced, by convergence or a
  cache rebuild, the length and validators change with it.
- A format's filename replaces the original member's extension with the server
  format catalog's extension. Each option carries its label and extension from
  that catalog; clients retain the stable wire enum and render the server data.
- Admin downloads and exports, consumer manifests and grants, and VFS describe a
  given format with the same filename, length, validators and authorization.

### Consumer Delivery

- Release manifests list the available formats for each item, and the client
  chooses. A content grant binds the chosen format's filename, SHA-256 and
  length, and its redemption returns that complete object.
- Negotiation is manifest-driven: clients choose an advertised format and its
  bound grant. The server does not select format through `Accept` negotiation.
- Authorizing one reconstructed member never authorizes the whole multi-output
  artifact. Whole-artifact delivery requires the dependency closure's complete
  authorization.
- Contract changes go through OpenAPI and the generated clients, with matching
  updates to the console's hand-written parser in the same change.

### VFS Formats

- Each VFS connection has a format setting: `stored` (the default) or
  `original`. Connections represent devices, and a device's emulators decide
  which formats it can read.
- With `stored`, a uniquely resolved whole-file kind supplies the native-runtime
  and stored-format rules. CHD projection uses its registered current recipe's
  typed parameter matcher; an older readable recipe falls back to original.
  Verified RVZ remains listed as RVZ across recipe changes, and CD track layouts
  list original members. With `original`, every member appears as the original.
- The layout lists one format per member. A member appears as the original when
  its format-specific name would collide with another path in the release under
  the layout's portable-path rules. Companion handling is computed at read time
  from the complete release path set: if it contains a descriptor or playlist
  (`.cue`, `.gdi`, `.toc`, `.ccd`, `.m3u`, `.m3u8`, `.mds`, `.pls` and similar),
  every member appears as
  original. ROMD neither inspects descriptor contents nor persists reference
  facts for this rule.
- Each entry reports its delivered length; its ETag is the delivered SHA-256.
- Paths under `stored` follow the stored format. When convergence gives a member
  a new stored format, for example an uploaded ISO becoming RVZ, its path changes
  from `.iso` to `.rvz`. Changing a connection's format setting also changes
  paths. Clients rescan, as they do after DAT changes, and anything they key by
  path does not follow the rename.
- HEAD, PROPFIND, bodyless conditional responses and unsatisfiable ranges are
  answered from verified metadata after current authorization, without opening
  CAS or a decoder. HEAD ignores `Range` and advertises the complete length of the
  delivered format.

### Admin Downloads And Exports

- Downloads and exports offer every available format. Tickets carry the logical
  ROM id and the chosen format. ZIP exports name each entry with its format's
  filename.
- Archives offer `original` and each stored format, the latter when available.
  `original` delivers every member's original bytes. A stored-format choice,
  such as “RVZ where available”, uses each member's bytes in that format where
  available, falling back to original at planning time for other members. The
  complete plan validates its resulting paths and
  binds each chosen target; redemption never changes a member's bound format.
  Durable export jobs persist this selection through restart. New jobs default
  to `original` unless another format is selected.

## Trade-offs

- **Paths move with formats.** A converged ISO becomes a `.rvz` path under
  `stored`, so clients rescan and lose anything keyed by the old path.
- **Mixed archives.** "RVZ where available" archives mix RVZ and original
  members, so consumers of an export check each entry's extension.
- **Checksums can be absent.** Legacy RVZs advertise no artifact MD5 or SHA-1
  until convergence re-verifies them.

## Rejected Alternatives

- **Switching a URL's bytes when a better format becomes available.**
  Rejected: clients verify and cache by path and validator, so a URL keeps its
  format, and replacement changes its validators.
- **Reusing logical hashes as artifact checksums.** Rejected: they describe
  different bytes, so a client verifying RVZ bytes against a logical hash
  would fail or be misled.
- **Inspecting descriptors, or persisting reference facts, to decide VFS
  naming.** Rejected: naming is derived at read time from the release's
  complete path set, so it needs no stored verdict to invalidate.
- **Aliases for old paths after a format change.** Rejected: one path, one
  format, and a rescan follows the change.

## Guardrails

- **`ConsumerVfsContentResolverTests`:** a bound target opens exactly that
  artifact, never a hash or CAS fallback, and a metadata or length mismatch
  fails before storage.
- **`ExportFormatSchemaTests` and `VfsConnectionFormatSchemaTests`:** the fresh
  baseline rejects unknown export selections and unknown or null VFS formats.
- **`FreshBaselineSchemaTests`:** provisioning starts from one fresh baseline
  and refuses unsupported existing schemas before changing data or objects.
- **`RomDeliveryPlannerTests`:** a ROM without an encoded artifact, including a
  companion on a disc platform, offers only `original`, and RVZ runtime
  applicability comes only from a uniquely resolved kind's descriptor.
- **`MediaKindResolverTests`, `MediaDeliveryCharacterizationTests` and
  `MediaPolicyLiteralArchitectureTests`:** shared fact-based kind resolution,
  explicit per-surface behavior and no copied media rules.
- **`RomMediaPolicyTests`:** the delivery policy covers exactly the platforms of
  the registered media policies.
- **Review:** each new delivery surface is checked against the shared filename,
  length, validator and authorization rule.
