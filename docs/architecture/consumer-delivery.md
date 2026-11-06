# Consumer Release Delivery

ROMD consumer delivery is full-object download only in the current phase. The
consumer API issues short-lived signed content grants from Release manifests;
the grant redemption endpoint streams the bound object as a complete response.

The per-item delivery-format contract requires a coordinated server and console
release. Existing console builds must update their handwritten manifest parser
when the server starts returning `items[].formats`; there is no format negotiation
or opt-in flag. The existing item fields and `runtime` metadata continue to
describe `original`, including when RVZ or CHD is also listed. A client selecting
an alternate format must use that entry's delivered path, hashes, length and
runtime metadata while retaining the original logical identity separately.
Recorded media/client qualification lives in
[media status](media-status.md#qualification-status); implementation and a
manifest format entry do not qualify emulator gameplay or another platform.

## Client Flow

1. Authenticate with the consumer API.
2. Browse owned content with the consumer browse endpoints:
   - `GET /api/me/library/systems`
   - `GET /api/me/library/systems/{systemKey}`
   - `GET /api/catalog`
   - `GET /api/titles/{titleId}`
   - `GET /api/collections`
   - `GET /api/collections/{collectionId}`
   - `GET /api/collections/{collectionId}/titles`
3. Select an owned Release from a title, catalog result, or collection result.
4. Request its manifest with `POST /api/releases/{releaseId}/manifest`.
5. Choose an available entry from the item's `formats` and download its
   `contentGrant.downloadUrl`. The existing item-level grant selects `original`.
6. Treat every content grant response as a full object. The expected success
   response is `200 OK` with `application/octet-stream`, `Cache-Control:
   private, no-store`, and `Content-Length` equal to the selected format's
   `sizeBytes`.
7. Read the selected format's `runtime`, or the manifest `runtime` for Original,
   to decide whether the client can build a
   local launch plan. Treat `runtime.launch` as absent when ROMD cannot infer a
   conservative launch target.
8. Verify the downloaded bytes before local use:
   - byte count equals the selected format's `sizeBytes`;
   - SHA-256 hex digest equals that format's `sha256`.
9. Materialize the verified file at the selected format's `relativePath` using
   client-owned install or cache behavior. Browser playback supports Original;
   selecting CHD for download does not establish browser playback support.

Unavailable manifest items do not include a content grant or SHA-256. They still
include `relativePath` and `sizeBytes` so clients can explain incomplete Releases
or preflight local layout.

## Delivery Contract

`ConsumerReleaseManifestDto` is the stable client contract for Release delivery:

- `releaseId`, `titleId`, `name`, and `revision` identify the selected Release.
- `systemKey` identifies the Release system so the client does not have to
  carry platform context from another browse response.
- `isComplete` describes whether ROMD has all files for the Release.
- `runtime.contentType` is a coarse file-layout classification such as
  `single_rom` or `unknown`.
- `runtime.launch` names the launch target only when ROMD can infer one
  conservatively. A missing launch target means the client must not guess.
- `runtime.packaging` describes the delivered manifest layout. The current
  supported value is `direct_files`, meaning each manifest item is already the
  file to materialize at `relativePath`.
- `runtime.minimumInstallBytes` is the expected local content footprint for the
  original manifest items. It does not sum alternate formats.
- `items[].relativePath` is the client-side path to materialize after
  verification. Treat it as a relative path only.
- `items[].role` identifies the file's runtime purpose. The first supported
  role is `rom`; more roles will be added when disc, BIOS, dependency, and patch
  behavior is modeled explicitly.
- `items[].sizeBytes` is the expected full object byte count.
- `items[].sha256` is a verification digest for available items.
- `items[].contentGrant.downloadUrl` is a short-lived anonymous download URL.
- `items[].contentGrant.expiresAt` tells the client when to request a fresh
  manifest instead of retrying an old URL.
- `items[].formats` lists `original` first for owned files, then `rvz` or `chd` only
  when the file retains an artifact in that encoding. Each entry has a `format`, `label`, `state`, `profileVersion`,
  `runtimeApplicability` and `relativePath`; an encoded entry also has its `fileExtension`. The label and extension
  come from the server's format catalog. The consumer portal labels its file buttons with `label`; no client displays
  `fileExtension` yet, and the console only validates both. Clients choose by `format` alone. An available entry
  includes its delivered `filename`,
  `sizeBytes`, `sha256` and content grant. RVZ replaces the member extension with
  `.rvz`; CHD uses `.chd`. Their hashes and length describe the artifact bytes. Original describes
  the logical bytes. Known MD5/SHA-1 values describe that same delivered format;
  unknown values are omitted.
- A format state is `available`, `preparing` or `unsupported`. Only available
  formats receive grants. Preparation requires recorded eligibility and job
  facts; a GameCube/Wii platform or `.iso` extension alone does not promise it.
  An `unknown` profile or empty runtime applicability makes no additional
  profile or runtime assertion.
- A single-file release whose platform registers a whole-file stored delivery
  (GameCube, Wii, PS2 and PSP today) gives each available format entry its own
  `runtime`: single-file launch metadata naming that entry's `relativePath`,
  and an install footprint equal to its `sizeBytes`. Multi-file releases,
  platforms without a stored delivery (cartridges, PS1 CD sets) and unavailable
  entries carry no format `runtime`. A format `runtime` describes the delivered
  file layout only; it makes no runtime claim beyond `runtimeApplicability`.
- CHD availability requires retained complete output verification matching the
  resident decoder tuple, fresh native-build verification and the host's decode
  capability. Runtime claims additionally require a uniquely resolved media kind.
  Current policy registers PS2 DVD CHD delivery and PCSX2 applicability; PSP UMD
  uses the shared DVD codec without a native runtime claim. PS1 CD member reads
  are qualified on Linux x64. A filename alone cannot authorize delivery.

For owned items, original `sizeBytes` is the measured logical byte count even if
the catalog's declared length differs. Missing items retain the catalog length
for preflight. All existing item fields continue to describe original bytes.

Temporary cache, permanent install, pinning, and eviction are client/runtime
policy decisions. Clients should derive them from manifest facts such as
`runtime.contentType`, `runtime.launch`, `runtime.packaging`,
`runtime.minimumInstallBytes`, item roles, available disk, emulator requirements,
and user preference.

The TypeScript consumer SDK generated from `web/schemas/consumer-v1.json`
exposes `issueConsumerReleaseManifest` for manifest issuance. Clients may redeem
`downloadUrl` with `fetch` or the generated `redeemConsumerContentGrant` helper,
but should read the response body as bytes.

## Full-Object Policy

`GET /delivery/content/{token}` is intentionally anonymous. The signed token is
the authorization. Redemption does not perform an ownership database lookup; it
validates the grant and streams the bound format.

An original grant follows the current retained representation only while the
ROM's logical SHA-256 and length still match its binding. An RVZ or CHD grant reads
only its bound artifact, checks its length and verifies its full SHA-256 while
streaming; a missing artifact fails instead of switching formats. Older signed
grants without a format claim retain original semantics. CHD redemption also
rechecks retained evidence, the bound delivery target and current decoder capability;
revocation or failed native readiness refuses the grant without another format.

When original bytes are stored as RVZ or CHD, redemption uses the logical content reader.
If the host's read decoder slots are occupied, redemption joins a bounded FIFO
for up to 5 seconds by default. A full queue or expired admission wait returns
`503` with `Retry-After: 15`. Retry while the grant remains valid, or request
a fresh manifest after its expiry. RVZ and CHD delivery stream artifact bytes without
using a decoder slot. Full sequential reads verify the delivered SHA-256 at EOF.
The console retries the current item up to three times, with at most 60 seconds
of retry waits. It honors `Retry-After` seconds or HTTP dates, defaults to 15 seconds
when the header is absent or invalid, and fails if the requested wait exceeds
its remaining budget. Earlier items remain staged during these retries;
401/404 still triggers the bounded manifest refresh and revalidation flow.
The formats are defined in the
[media delivery decision](../decisions/media-delivery.md#delivery-formats).

The content route does not expose file ids, storage keys, CAS keys, or hashes in
the URL. Storage identity stays inside the signed token. The manifest's
`sha256` field is for client verification, not URL construction.

Do not rely on range behavior for ROM content in this phase. Range requests still
return `200 OK` full content. The API does not advertise `Accept-Ranges`, does
not return `Content-Range`, and does not document `206 Partial Content`.

## Browser Clients

Production browser clients must configure consumer-host CORS origins with
`Romd:ConsumerHost:CorsOrigins`. The consumer and admin hosts have separate CORS
policies; adding a consumer origin does not widen the admin API surface.

The reference web consumer lives under `web/packages/romd-consumer-app`. During
development, run it from `web/` with `pnpm dev:consumer`; its Vite dev server
uses `http://localhost:5174`, which is included in the development consumer CORS
origin set.

`Romd.Consumer.Host` also needs `Romd:ConsumerDelivery` signing settings before
it can issue manifest content grants. The split consumer host includes
development-only local signing settings in
`src/Romd.Consumer.Host/appsettings.Development.json`. For an explicit shell
override, use environment variables such as:

```sh
Romd__ConsumerDelivery__SigningKeyId=dev-local
Romd__ConsumerDelivery__SigningSecret=ConsumerDeliveryDevelopmentSecret_DoNotUseInProduction_32chars!
Romd__ConsumerDelivery__SignedUrlTtlMinutes=10
```

## Ordered disc sets

[Disc-set architecture](disc-sets.md) owns published ordered membership, descriptor
intake/repair, original/Derived file provenance, partial-install consent and exact
snapshot renewal/access. A set does not grant access to any member independently
of its published Library. The single-release contract above remains unchanged.

Clients discover `/api/disc-sets?titleId=...`, inspect status and request an
explicit selection manifest. They verify the derived M3U and each bound file
locally. [Console acquisition](../../clients/romd_console/docs/original-disc-sets.md)
owns install/repair and local identity; production group playback still requires
separate disc-switch/shared-save acceptance in the [qualification matrix](media-status.md#qualification-status).
