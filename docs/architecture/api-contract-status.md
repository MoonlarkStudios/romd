# API Contract Implementation Status

The [API contract policy](../decisions/api-contract-policy.md) defines the target for both JSON APIs. The route/OpenAPI migration ratchets are empty. They check contract shapes and do not prove every growing collection, external-provider search or transactional workflow meets the policy. This page records remaining implementation work; it grants no exceptions.

The core migration aligns resource routes and methods, strict binding, shared problem envelopes, opaque IDs, common cursor Pages, accepted-job references and the existing conditional-write workflows. Backend changes pair with committed OpenAPI, both generated web clients and actual web/console callers in the same change. Provider game suggestions publish their coverage and finite budgets; incomplete evidence does not authorize automatic identity acceptance. Exact game resolution is a separate operation. Conditional writes reject malformed `If-Match` syntax with a field-keyed 400, valid unsatisfied tags with 412, and missing headers or a lone wildcard with 428.

Systems and companies now use bounded keyset Pages on both hosts. System setup
also pages and searches aliases on the server instead of embedding all aliases.
Admin regions and languages embed a bounded first alias Page per row, continue it
through each row's alias collection, search names, keys and every alias on the
server, and bound alias-write lock waits with a retryable 409; see
[shared reference data](shared-reference-data.md). Query names are closed on
both hosts: every `/api` operation refuses an undeclared or repeated scalar query name
before binding and documents the 400.
Complete reference catalogs transfer through a bounded manifest and six
revision-pinned page resources, with count and canonical digest validation before
complete cache replacement. The manifest validator and published catalog identity
are distinct. Existing complete offline cache identities and publisher
transactions remain intact; see [reference snapshots](shared-reference-data.md#client-contract-and-snapshots).

## Remaining work

| Priority and surface owner | Current behavior and affected contract | Acceptance for a follow-up |
| --- | --- | --- |
| P2 — Catalog and library facets; admin, consumer and clients | `/api/catalog/filters` returns every distinct genre, publisher label and ingested region/language. `/api/me/library` embeds every distinct genre. These are data-grown facets rather than fixed reference vocabularies. Owners: search/library repositories and filter/navigation clients. | Provide paged searchable facets with documented limits and scope-bound cursors. Preserve counts and complete navigation/filter choices across all clients. Test data beyond page boundaries and library isolation. |
| P2 — Jobs evidence; admin API, realtime and SPA | `Job.errors` exposes all accumulated errors in polling, list rows and realtime payloads. Errors append without a collection maximum. Owners: job domain/persistence mapping, job endpoints, realtime contract and clients. | Separate complete keyset error evidence from bounded summaries without losing retry identities. Align polling and realtime, with event version changes where required. Test large failed jobs and full retry/export behavior. |
| P3 — Persistence lock namespaces | DAT catalog coordination and artwork request deduplication share advisory lock family 146 with unrelated hashed keys. Equal keys can cause unrelated operations to block or report busy. Taxonomy key minting already uses its own family 150. | Assign separate named families and test independent cross-domain acquisition, same-domain serialization and deduplication. |
| Separate worker behavior — metadata rematerialization | Changing `Enrichment:GlobalSourcePriority` at restart changes metadata-policy validators but does not automatically rematerialize existing title metadata/media. See [backend mutations](backend-mutations.md#durable-metadata-work). | Durable worker-owned configuration-change reconciliation, restart deduplication/recovery and library invalidation tests. Keep this separate from HTTP shape cleanup. |

## Collections requiring explicit classification

Fixed roles, supported providers and built-in-only reference vocabularies remain bounded arrays. Complete manifest/definition/configuration state must not be partially installed or replaced to satisfy a paging check. Library attachments enforce a 500-member unique replacement bound; featured collections inherit that bound.

Operational diagnostics already expose explicit truncation indicators and finite budgets. Consumer system detail currently selects twelve media previews without publishing that preview bound. Reference snapshots now have explicit coherent paged transfer. Atomic configuration, diagnostic samples and media previews still need explicit classification; their existing shapes do not imply new policy exemptions.

## Compatibility and qualification boundaries

The supported pairing remains same-commit or same-release first-party clients and server. Record a version-skew decision before independent console distribution or 1.0, whichever comes first; see [release readiness](../maintainers/release-readiness.md).

Validation receipts belong in the pull request, not this document. Structural ratchets do not establish a full-suite, real-host, container-engine, prepared-demo, live-provider or performance qualification pass. Keep unavailable hosted/prepared-data checks separate from remaining product-contract work.
