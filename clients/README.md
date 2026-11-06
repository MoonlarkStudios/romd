# ROMD clients

Native client applications that live outside the React web workspace.

| Client | What it is | Status |
| --- | --- | --- |
| [Ottercade](romd_console/README.md) (`romd_console/`) | Flutter console app for controllers and TVs | Experimental |
| [ROMD Mount](romd_mount/README.md) (`romd_mount/`) | macOS development CLI that mounts the consumer WebDAV library | Parked development tool, not a supported client |

Keep each client's toolchain inside its own directory. Do not add Flutter or
other native-client tooling to the `web/` pnpm workspace.
