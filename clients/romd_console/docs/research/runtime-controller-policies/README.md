# Runtime Controller Adapter Policies

This directory preserves reproducible, adapter-specific controller correlation
research. A policy applies only to its frozen artifact, source revision,
platform, architecture, input backend, SDL build, mapping database, and ROMD
provider behavior. Missing identity is evidence; provider ordinals are never
promoted by analogy.

| Adapter | Runtime build | Platform/backend | Classification | Policy id | Validated | Status |
| --- | --- | --- | --- | --- | --- | --- |
| DuckStation | v0.1-10998 | macOS 13.3+, SDL 3.4.2, arm64/x86_64 | `uncorrelated` | none | 2026-07-10 | current |
| RetroArch | 1.22.2 Metal | macOS 10.13+, GameController/MFi, arm64/x86_64 | `uncorrelated` | none | 2026-07-10 | current |

Use [the research template](templates/adapter-policy-research-template.md) for
new adapters and revalidation. A current entry means the documented fallback is
current; it does not imply forced references are approved.
