# Consumer Dart wire generation pilot

Status: evaluated and not adopted. Existing handwritten Consumer clients remain
in place, and the strict identity, access, and manifest adapters remain the
only authoritative wire boundaries.

## Boundary

The pilot generated a Consumer-only `dart-dio` transport and DTO package from
`web/schemas/consumer-v1.json`. It was never imported by production code and is
not retained in the repository because it did not meet ROMD's analysis and
wire-accuracy requirements. Generated deserialization is never an access
decision: a handwritten adapter must validate response completeness, authority
identity, canonical public ids, and conditional allow/revoke fields before
returning an authoritative domain result.

This pilot does not change profile access semantics. A profile-local game row
is the per-release cached grant, and only the handwritten acquisition and
launch boundaries may create or update it. There is no background batch or
AccessRevision contract.

## Evaluated toolchain

The bounded implementation used OpenAPI Generator CLI 7.23.0 with a verified
SHA-256, Temurin Java 21 through mise, and an isolated generated-package
dependency lock. Build runner ran only in the generated package or check
directory. The preferred shape, if the blockers below are resolved upstream,
remains a repository-owned mise task that invokes pinned direct `dart-dio`
generation and a matching CI drift check. The annotations/build-runner wrapper
adds the same Java generator dependency plus root build-runner coupling without
a material advantage for ROMD.

The evaluated command shape from `clients/romd_console/` was:

```sh
mise run api:consumer:generate
mise run api:consumer:check
```

Those tasks are not retained because the generated result failed acceptance.

The ASP.NET OpenAPI 3.1 document describes numeric values as
`integer|string`/`number|string`. Dart OpenAPI Generator produced uncompilable
wrapper types for those schemas. Normalizing the unions to numeric-only types
made generation compile but was lossy: schema-valid numeric strings would then
be rejected. Changing the canonical contract may solve this only if backend
serialization proves strings impossible; the pilot did not make that product
contract change.

## Evaluation result

The generated Dio APIs provided request cancellation, explicit headers,
status-code access, raw response retention on parse errors, and configurable
redirect/status handling. Timeout values are inherited from the shared Dio
configuration; generated methods expose cancellation but no per-call timeout
parameter. Strict ROMD adapters therefore retain their operation-local timeout
and abort ownership.

They are not sufficient as an authorization parser:

- the Consumer OpenAPI document currently declares no security scheme, so the
  generator does not attach bearer authentication; the caller must supply the
  header;
- extra JSON fields are accepted;
- optional absent and explicit-null fields collapse to `null`;
- public identifiers remain unvalidated strings;
- generated path parameters are interpolated without URI-component encoding;
- generated access models do not enforce the conditional one-release
  allow/revoke shape or exact server/release/title identity;
- the current Consumer schema contains no enums, so unknown-enum behavior is
  not presently exercisable. Generation is pinned with
  `enumUnknownDefaultCase=false` for fail-closed behavior if an enum is added.

The generator emitted the complete Consumer package rather than four selected
API groups. Its supporting barrel files are built from the complete schema and
import every generated API; selecting APIs while retaining supporting files
would require custom templates or handwritten generated aggregators. The pilot
therefore accepts full Consumer output and focuses regression tests on server
identity, release access, manifest delivery, and catalog search. It does not
generate or modify the Admin client.

Package-local analysis exited 2 with 18 generator-owned unused-import,
unused-field, and unused-parameter warnings. The warnings were neither
suppressed nor patched in derived files. Generation also interpolated path
parameters without URI-component encoding, accepted extra fields, collapsed
absent and explicit-null optionals, and did not isolate anonymous discovery
from credentials inherited through a shared Dio instance.

Focused characterization plus the existing strict identity/access/manifest
suites passed 43/43. Deterministic regeneration matched its generated output.
Those positive signals do not outweigh lossy schema normalization and
non-clean analysis. Keep the three strict clients handwritten. Reconsider
generated transport only for ordinary Store/catalog APIs after an upstream or
explicitly reviewed generator solution produces analyzer-clean output without
changing the accepted wire contract. Do not migrate server identity,
one-release authorization, or manifest validation to generated parsing.
