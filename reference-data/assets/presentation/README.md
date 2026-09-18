# Built-in reference artwork

ROMD's server embeds these reviewed system logos and rating marks. Attribution
files stay with the artwork. Canonical definitions in `reference-data/catalog/`
reference paths relative to this directory. Validate them with
`node scripts/presentation/validate.mjs`.

The worker installs defaults into the effective reference catalog and publishes
immutable asset URLs. Web clients use those URLs. Flutter retains the downloaded
snapshot and verified assets for offline use. Neither client ships a competing
per-system artwork bundle; missing or explicitly hidden artwork uses generic
client rendering.
