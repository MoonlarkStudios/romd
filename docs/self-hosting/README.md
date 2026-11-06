# Run your own ROMD server

ROMD runs on one Linux server with Docker Compose: PostgreSQL, a worker, the
admin and consumer portals, and an isolated browser player. You manage the
server, storage, HTTPS ingress and backups. People using your library need only
their account and a browser; Ottercade is an optional, experimental console app.

## Choose your starting point

| What you are doing | Read this |
| --- | --- |
| Evaluating ROMD before a qualified release is available | [Run from source](../development/getting-started.md) with Docker or rootless Podman |
| Installing a released server | [Production deployment](production-deployment.md#release-artifacts) |
| Understanding settings and defaults | [Configuration reference](configuration.md) |
| Adding your first games and people | [Your first library](../features/first-library.md) |
| Upgrading an installation | [Upgrade procedure](production-deployment.md#upgrade) and that release's notes |
| Protecting or recovering your server | [Backup and restore](production-deployment.md#backup-and-restore) |
| Managing disc conversion or native-media recovery | [Media processing](media-processing.md) |

The initial supported-release qualification target is **Linux amd64**. Source
builds and passing development checks do not qualify a downloaded release.
Native console builds remain experimental. Check the
[release notes](https://github.com/MoonlarkStudios/romd/releases) for the support
and upgrade scope of the version you plan to install.

## Installation path

1. Read the [prerequisites](production-deployment.md#prerequisites-and-configuration):
   Docker/Compose, local storage, HTTPS origins and installation secrets.
2. Obtain a versioned deployment bundle and verify its checksum. Keep the bundle
   and its pinned image references together; follow [release artifacts](production-deployment.md#release-artifacts).
3. Fill in the installation configuration and [choose ingress](production-deployment.md#choose-ingress).
   The examples cover an existing proxy, HTTPS nginx and Cloudflare Tunnel.
4. Follow [Fresh installation](production-deployment.md#fresh-installation),
   including validation and verification through your actual ingress.
5. Sign in to the admin portal and build [your first library](../features/first-library.md).
6. Schedule backups in your infrastructure and rehearse the restore procedure.

The production runbook owns the commands and operational requirements. The
development stack has separate credentials, ports and storage; use its own
[setup guide](../development/getting-started.md) when working from a checkout.
