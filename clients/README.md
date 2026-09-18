# ROMD Clients

Client applications that are not part of the React web workspace live here.

## Layout

- `romd_console/`: Flutter-based 10-ft console frontend for browsing, caching,
  and launching ROMD consumer library content.

Keep client-specific toolchains inside each client directory. Do not add Flutter
or other native-client tooling to the `web/` pnpm workspace.
