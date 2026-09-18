# Catalog visual baselines

The `pre_refactor_*` images are immutable historical evidence captured before
the Discover shell became the live Catalog surface. They are intentionally not
collected by an active `*_test.dart` matcher.

SHA-256:

- `pre_refactor_dark/current_search.png`:
  `51b30952602c7dd54228013a5db38ce23a344d736743c4292cd13044b7d479a1`
- `pre_refactor_dark/current_catalog.png`:
  `ee6f5167606987fba1acbf8871741145937056e968aa2de72b7a966f64b9f1d2`
- `pre_refactor_light/current_search.png`:
  `92864366b14c90fe51553eac4cec4877abf44351c4b75fa2ff799277aeec37e5`
- `pre_refactor_light/current_catalog.png`:
  `56537463e5ef7f57dc8dcd4887ffde5d038cdc2bff2cba01a7b7d0c74619e47b`

The `discover_foundation_*` images are immutable historical Gate 1 foundation
checkpoints. The archived capture sources
(`archived_current_catalog_golden_source.dart`,
`archived_discover_foundation_golden_source.dart`) were removed together with
the legacy implementation they exercised (`category_browse.dart`,
`search_screen.dart`, `discover_legacy_navigation.dart`); the images above
remain the immutable evidence. The per-surface living-catalog goldens are the
active visual contract.
