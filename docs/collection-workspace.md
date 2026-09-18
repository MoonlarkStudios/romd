# Shared collection workspace

Collections are reusable curated experiences attached to audience libraries. The
Admin workspace lives at `/collections/:collectionId`; old
`/collections?collection=...` links redirect there.

- **Build:** Search the catalog beside the ordered collection, select up to 24
  games, change order, and edit per-game notes. Smaller screens offer a Find games /
  Collection toggle. Details and artwork are edited from the workspace header;
  artwork can be chosen from collection games.
- **Audiences:** Attach the collection to libraries and inspect their visible / total
  game counts and featured placement. Unlink an audience using the same unlink
  icon as the library view; other attachments retain their order and featured
  settings. Library links open attachment management.
- **Preview:** View owned games for an attached audience. Investigate a collection
  game using the existing library evaluator, including rating and ownership
  explanations. Invalid or rebuilding library policies suppress the saved preview.

Edits save immediately and affect every attached library. Batch additions are
individual requests, not an atomic operation: successful additions remain saved,
while failed and unattempted games remain selected. System collections stay
read-only. Existing role permissions still apply; audience management and preview
require Admin access.

API client scope: **none**. This uses existing collection, library attachment,
preview, and evaluation contracts. There are no backend, schema, or generated
contract changes. Game investigation searches the existing evaluator and follows
its cursor until the exact title is found. A direct title evaluation endpoint
could reduce that work for large catalogs.

## Verification

- Web lint passed. Explicit-inclusion Biome check formatted and checked the nine
  touched Collections/evaluation TypeScript files because the repository formatter
  has the documented negative-only includes issue.
- Full web suite passed: 78 files, 499 tests. Collection coverage includes canonical
  and legacy navigation, system protection, creation, artwork preservation,
  partial batch failure, ordering, audience preview/explanation, and successful
  and failed audience unlinking.
- Full web production build passed. Existing React test `act(...)`, SignalR
  annotation, and bundle-size warnings remain.
- Local `http://localhost:5137/collections` returned HTTP 200.
- Browser visual verification was not performed: no browser surface was available.
  Review desktop width stability, mobile switching, modal layout, and artwork in
  the local demo before merging.
