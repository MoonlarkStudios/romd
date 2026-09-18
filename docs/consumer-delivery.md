# Consumer Release Delivery

ROMD consumer delivery is full-object download only in the current phase. The
consumer API issues short-lived signed content grants from Release manifests;
the grant redemption endpoint streams the bound object as a complete response.

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
5. For each manifest item where `isAvailable` is `true`, download
   `contentGrant.downloadUrl`.
6. Treat every content grant response as a full object. The expected success
   response is `200 OK` with `application/octet-stream`, `Cache-Control:
   private, no-store`, and `Content-Length` equal to the manifest item
   `sizeBytes`.
7. Read manifest `runtime` metadata to decide whether the client can build a
   local launch plan. Treat `runtime.launch` as absent when ROMD cannot infer a
   conservative launch target.
8. Verify the downloaded bytes before local use:
   - byte count equals `sizeBytes`;
   - SHA-256 hex digest equals `sha256`.
9. Materialize the verified file at `relativePath` using client-owned install or
   cache behavior.

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
  manifest items.
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
validates the grant and streams the bound CAS object.

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
