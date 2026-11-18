# Run your own ROMD server

ROMD runs on one Linux server with Docker Compose: PostgreSQL, a worker, the
admin and consumer portals, and an isolated browser player. You manage the
server, storage, HTTPS ingress and backups. People using your library need only
their account and a browser; Ottercade is an optional, experimental console app.

## Choose your starting point

| What you are doing | Read this |
| --- | --- |
| Installing a released server | [Quick start](production-deployment.md#quick-start) |
| Trying ROMD without a release | [Run from source](../development/getting-started.md) with Docker or rootless Podman |
| Understanding settings and defaults | [Configuration reference](configuration.md) |
| Adding your first games and people | [Your first library](../features/first-library.md) |
| Upgrading an installation | [Upgrade procedure](production-deployment.md#upgrade) and that release's notes |
| Protecting or recovering your server | [Backup and restore](production-deployment.md#backup-and-restore) |
| Managing disc conversion or native-media recovery | [Media processing](media-processing.md) |

## Which release to install

There is no stable release yet. The [releases page](https://github.com/MoonlarkStudios/romd/releases)
lists qualified release candidates (`v0.1.0-rc.N`), marked as prereleases;
install the newest one. Each is a complete, immutable release that passed
packaged installation, browser playback, restart and backup/restore on
**Linux amd64**, the only qualified platform. Its notes state what it supports
and whether you can upgrade to it from an earlier candidate; until they say so,
plan a fresh installation.

`v0.1.0-rc.8` can fail its first start on slow storage, reporting that
`romd-worker` is unhealthy. Run the same start command again and it completes.
Later candidates fix this.

Source builds and passing development checks do not qualify a downloaded
release. Native console builds remain experimental.

## Installation path

1. Read the [prerequisites](production-deployment.md#prerequisites-and-configuration):
   Docker/Compose and local storage.
2. Obtain a versioned deployment bundle and verify its checksum. Keep the bundle
   and its pinned image references together; follow [release artifacts](production-deployment.md#release-artifacts).
3. Create `.env` as the [quick start](production-deployment.md#quick-start)
   shows: the host name your devices use and the first administrator. The
   [built-in ingress](production-deployment.md#built-in-ingress) serves ROMD over
   HTTPS on your local network; overlays cover three host names, an existing
   proxy and Cloudflare Tunnel.
4. Follow [Fresh installation](production-deployment.md#fresh-installation),
   including validation and verification through your actual ingress.
5. Sign in to the admin portal and build [your first library](../features/first-library.md).
6. Schedule backups in your infrastructure and rehearse the restore procedure.

The production runbook owns the commands and operational requirements. The
development stack has separate credentials, ports and storage; use its own
[setup guide](../development/getting-started.md) when working from a checkout.
