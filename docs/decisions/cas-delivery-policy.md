# CAS Delivery Policy for Consumer ROM Content

Status: historical

Current consumer Release delivery is documented in
[`docs/consumer-delivery.md`](../consumer-delivery.md). This record captures an
earlier design direction and is not the current client contract.

Related issues: #10, #7, #12

## Context

ROMD stores file bytes in `IContentAddressableStore` by SHA-256. The current
file-system implementation may store either `{hash}.zst` or `{hash}.raw`, but
`RetrieveAsync` returns a decompressed `HashVerifyingStream` with `CanSeek ==
false`. Current ROM download authorization is checked through
`IRomRepository.IsAccessibleAsync`, then the endpoint streams the CAS retrieval
result as a full file response.

That makes CAS a good source of verified bytes, but not a stable random-access
backing store. Compressed CAS blobs cannot satisfy arbitrary byte ranges without
decompressing earlier bytes, and the current public CAS API intentionally does
not expose physical blob paths or seekable streams.

## Decision

ROMD models consumer ROM delivery as install/cache-first:

1. `LaunchOption`: what an authenticated consumer can choose for a title, such
   as unavailable, immediate small-content download, or local install/cache.
2. `InstallManifest`: the files a client must fetch, verify, and place in its
   launch cache for a selected option.
3. `ContentGrant`: a short-lived authorization to fetch one CAS object named by
   storage identity, hash, and size.
4. Launch cache/local install: the expected destination for most ROM content
   before emulator launch.

Signed URLs are content download grants. They are not the main "play" primitive
and should not be described as launch URLs. A client launches from verified local
content whenever the selected option requires an install manifest.

Do not promise `Range` or `206 Partial Content` from compressed CAS blobs. Range
support is a property of a launch-cache artifact or another explicit
range-capable delivery surface, not the canonical CAS object.

## Launch Options

Consumer browse/detail APIs expose launch option summaries, not storage details
or signed URLs. The option vocabulary is:

- `Unavailable`: the consumer cannot currently launch or install the title.
- `ImmediateDownload`: small content may be fetched as a full-object download and
  launched immediately by capable clients after verification.
- `InstallRequired`: the client should request an install manifest, download the
  listed content grants, verify hashes and sizes, cache locally, and launch from
  that cache.

Current Phase 1.7 behavior is intentionally conservative: owned content is
advertised as `InstallRequired`, and unowned content is advertised as
`Unavailable`. Phase 2 can add the `ImmediateDownload` optimization for small
content after manifest and grant issuance are implemented.

## Small ROM Immediate Download

Small ROM support is an optimization, not the default architecture:

- Keep CAS as the source of truth.
- Resolve authorization and storage identity before issuing a content grant.
- Redeem the grant by retrieving the CAS object and returning verified bytes as a
  full `200 OK` response.
- Return `200 OK` only; omit `Accept-Ranges` or set `Accept-Ranges: none`.
- If a client sends `Range`, ignore it for this path and still return `200 OK`,
  or return a clear client error if the endpoint is explicitly install-only. Do
  not return `206`.
- Use this path only for small cartridge-style files. The initial configurable
  threshold is 64 MiB.

## Install Manifest and Launch-Cache Strategy

For most ROM files, large files, disc images, or clients known to perform seeks:

- Add an authenticated manifest endpoint for a selected launch option.
- Resolve the manifest from user, library, title, ROM, file, SHA-256, size, and
  local install metadata. Do not expose raw storage ids in browse/detail DTOs.
- Issue content grants for individual CAS objects in the manifest.
- Clients download each granted object, verify SHA-256 and byte count, and write
  it to a local launch cache before emulator launch.
- If ROMD hosts a launch-cache artifact, build or reuse an uncompressed seekable
  artifact keyed by byte identity, not by request identity.
- Materialize hosted artifacts by streaming from CAS into a temp file, hashing
  during write, and atomically publishing only after the SHA-256 and byte count
  match the `Files` row.
- Treat hosted artifacts as disposable cache entries. Cache misses rebuild from
  CAS; deletion never affects CAS.

Suggested cache key:

```text
launch-cache:v1:{sha256}:{size}:raw
```

The key intentionally excludes user and library ids so identical content can
deduplicate. Authorization is enforced by the content grant and request
validation, not by hiding artifacts behind per-user paths.

## Range Rules

`206 Partial Content` is supported only when all are true:

- The request is for an explicit range-capable launch-cache artifact URL.
- The content grant is valid and unexpired.
- The artifact metadata matches the requested ROM `FileId`, SHA-256, and size.
- The artifact exists as an uncompressed seekable file, or can be built before
  responding.
- The request includes a syntactically valid satisfiable `Range` header.

All CAS-backed direct download responses are full responses. Even if the current
CAS object happens to be stored as `.raw`, the public CAS API still returns a
non-seekable verifying stream, so direct downloads remain consistently
non-range.

## Expiry, Eviction, and Scope

Content grant shape:

- Manifest endpoint: `POST /api/titles/{titleId}/launch-options/{option}/manifest`.
- Grant redeem endpoint: `GET /delivery/content/{token}`.
- Do not put `FileId`, SHA-256, user id, library id, or cache keys in the URL
  path or query string outside the signed token.
- The manifest response returns launch option metadata, install manifest items,
  content grant download URLs, and expiration times. It does not expose storage
  ids outside signed grant payloads.
- Grant issuance resolves a `ConsumerContentGrant` before signing. The grant
  binds user id, library id, title id, ROM id, `FileId`, SHA-256, size, and
  launch option kind. The issuer signs only this resolved grant and reads
  TTL/signing options from configuration.

Content grant expiry:

- Use short bearer-token lifetime, default 10 minutes.
- Configure through `Romd:ConsumerDelivery:SignedUrlTtlMinutes`.
- Include `iat`, `exp`, `kid`, `jti`, `sub`, `library_id`, `title_id`,
  `rom_id`, `file_id`, `sha256`, `size`, and `launch_option_kind` claims in the
  signed payload.
- Allow clock skew only on the order of seconds, not minutes.

Signing options:

- Signing secret comes from `Romd:ConsumerDelivery:SigningSecret`.
- Signing key id comes from `Romd:ConsumerDelivery:SigningKeyId`.
- Use HMAC SHA-256 initially. Keep key id in the token to allow rotation.
- Validate signatures with a constant-time comparison.

Redeem response behavior:

- Valid small/full content grant: `200 OK`, `Cache-Control: private, no-store`,
  no `Accept-Ranges`, and no `206`.
- Valid range-capable content grant with a satisfiable range: `206 Partial Content`,
  `Accept-Ranges: bytes`, and `Cache-Control: private, no-store`.
- Expired token: `401 Unauthorized`.
- Tampered token or unknown key id: `401 Unauthorized`.
- Token valid but user/library/title/ROM scope no longer allowed when rechecked:
  `403 Forbidden`.
- Valid token for missing content or missing launch artifact that cannot be
  rebuilt: `404 Not Found`.
- Invalid or unsatisfiable range on a range-capable launch-cache URL:
  `416 Range Not Satisfiable`.

Artifact retention:

- Track artifact path, SHA-256, size, created time, last access time, and build
  status.
- Use sliding expiration from last access, default 24 hours.
- Add a maximum cache size and evict least-recently-used artifacts first.
- Clean partial temp files on failed builds and via periodic cleanup.

Access scope:

- Manifest and grant issuance require normal ROM access. For scoped users, use
  `IRomRepository.IsAccessibleAsync(romId, currentUser.LibraryId)`.
- The content grant binds to the access scope used at issuance time.
- For normal users, include `library_id` in the token. If a user has no valid
  library scope, do not issue an install manifest or content grant.
- Short expiry is the primary revocation boundary. Higher-risk deployments can
  re-check access before serving.

## Security Guardrails

- Never expose raw cache paths or CAS paths in API responses.
- Never allow hash-only artifact URLs.
- Artifact existence does not authorize access. Every request validates the
  content grant before opening the artifact.
- Use constant-time signature validation and support signing key rotation
  through a key version claim.
- Atomically publish artifacts only after integrity verification.
- Store launch-cache artifacts outside public static roots.
- Log artifact builds and redemptions with user id, ROM id, file id, and cache
  key, but never log the full signed grant URL.

## Implementation Notes

- The existing direct ROM download endpoint has the right authorization shape,
  but consumer Phase 2 delivery should use launch options, install manifests, and
  content grants instead of exposing broad file primitives.
- Manifest and launch-cache services should sit above CAS and below endpoints.
  They should not require changing `IContentAddressableStore` to expose blob
  paths.
- The current export artifact cleanup pattern is a useful model for scheduled
  launch-cache eviction, but launch-cache metadata should be separate from
  export jobs.
