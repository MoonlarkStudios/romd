# Bundled shared reference data

`reference-data.json` is the exact authenticated snapshot from publication 3006
of https://moonlarkstudios.github.io/romd-dat-data. It was verified with the
existing TUF bootstrap root/cache using the companion `distribution reference-data`
reader. Its SHA-256 is
`a0f539d85ed9db1619ce1e44574a1973436070769e23afcad3027ba0cf0cbd4f`.

Editable category dictionaries live in MoonlarkStudios/romd-dat-catalogs; this
copy is derived, not independently maintained. Publisher revision:
`249ac9dd0bb25fd9ba2faf9eb9795ab963a16226`. Reader revision:
`fe8077094457f9d41fbd3baa541402f46a28ea72`.

Fresh worker startup uses this embedded snapshot without a network dependency.
Updating the bundled bytes requires verifying a new signed publication,
reviewing its changes, updating the pinned hash/version, and running seed tests.
The hash is a build-time pin, not a substitute for authenticating remote updates.

The worker's shared initializer records the applied document and database-ID
bindings once. Migration 4 captures whether this is an established database
before publishing its new schema version. Existing installations adopt matching
IDs without reseeding; missing and ambiguous entries remain unbound. Fresh rows,
aliases, and the baseline commit together. Later starts do not recreate deleted
categories or aliases. This baseline is the prerequisite for reviewed updates;
it does not itself fetch or apply a newer remote snapshot.
