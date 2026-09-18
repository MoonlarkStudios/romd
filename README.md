# ROMD

A DAT-driven ROM management platform with split admin, consumer, and worker hosts.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 24+](https://nodejs.org/)
- [pnpm 10.28+](https://pnpm.io/)
- [Flutter](https://flutter.dev/) (optional, for the console client)
- [Docker](https://www.docker.com/) with Compose (optional, for containerized runs)
- [mise](https://mise.jdx.dev/) (optional, for environment standardization)

## Production deployment

Use the versioned deployment bundle and published container images. ROMD runs
PostgreSQL, a worker, admin and consumer portals, and a separate browser player.
The portable production Compose does not build source or require a specific
hosting provider. Configure HTTPS ingress and installation secrets following the
[production deployment runbook](docs/production-deployment.md).

Keep your host paths, domains, tunnels and backup schedules in your own
infrastructure repository. Development uses the separate configuration below.

## Daily Development

For normal app work, run the backend split-host topology in Docker and run Vite locally for frontend HMR:

```bash
docker compose -f compose.dev.yaml up --build romd-worker romd-admin romd-consumer
```

Then start the local frontends in separate terminals:

```bash
cd web
pnpm install
VITE_ROMD_ADMIN_API_ORIGIN=http://localhost:11337 pnpm dev
```

```bash
cd web
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:11338 pnpm dev:consumer
```

`compose.dev.yaml` uses local-only defaults, enables CORS for the Vite ports, keeps the worker private, and stores CAS/keys in `romd-dev-data` and PostgreSQL application/Hangfire data in `romd-dev-postgres`. It defaults admin to `http://localhost:11337` and consumer to `http://localhost:11338` to avoid common low-port collisions.

## Source Quick Start

```bash
# If using mise
mise install

# Restore and build
dotnet restore
dotnet build

# Run tests
dotnet test

# Start the local PostgreSQL dev server (required by hosts;
# see docs/split-host-topology.md "Dev Database")
mise run db:up

# Set this in every terminal that runs a host
export Romd__DataDirectory="$PWD/.data/romd"
```

Start the worker in one terminal:

```bash
dotnet run --project src/Romd.Worker.Host
```

Start the admin API host in another terminal:

```bash
dotnet run --project src/Romd.Admin.Host --urls http://localhost:5000
```

Start the admin app:

```bash
cd web
pnpm install
pnpm dev
```

The admin app runs at `http://localhost:5137` and proxies to the admin API at `http://localhost:5000`.

For the consumer surface, run `Romd.Consumer.Host` on a separate port and point the consumer Vite proxy at it:

```bash
dotnet run --project src/Romd.Consumer.Host --urls http://localhost:5002
```

```bash
cd web
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:5002 pnpm dev:consumer
```

The consumer app runs at `http://localhost:5174`.

See [Split Host Topology](docs/split-host-topology.md) for the full local, Docker, and production runbook.
See [Configuration](docs/configuration.md) for the supported `appsettings.json`
sections, environment-variable equivalents, defaults, and secret-handling guidance.

## Project Structure

```
src/
|-- Romd.Domain                 # Domain entities, value objects, domain logic
|-- Romd.Application.Common     # Shared CQRS, ids, pagination, security, ports
|-- Romd.Admin.Application      # Admin and curator use cases, ports, read models
|-- Romd.Consumer.Application   # Consumer browse, account, auth, collection use cases
|-- Romd.Infrastructure         # External concerns: DB, file system, external tools
|-- Romd.Hosting                # Shared host composition and endpoint modules
|-- Romd.Admin.Host             # Admin HTTP/API/SignalR host
|-- Romd.Consumer.Host          # Consumer HTTP/API host
`-- Romd.Worker.Host            # Non-HTTP background worker host

clients/
`-- romd_console                # Flutter 10-ft console frontend

tests/
|-- Romd.Domain.Tests
|-- Romd.Application.Tests
|-- Romd.Infrastructure.Tests
`-- Romd.Hosting.IntegrationTests
```

## Documentation

- [Split Host Topology](docs/split-host-topology.md): local commands, production ownership, PostgreSQL/Hangfire notes, and admin realtime relay behavior.
- [Consumer Delivery](docs/consumer-delivery.md): signed consumer content delivery and full-object response policy.

## Development

### Build
```bash
dotnet build
```

### Test
```bash
dotnet test
```

### Run

```bash
dotnet run --project src/Romd.Worker.Host
dotnet run --project src/Romd.Admin.Host --urls http://localhost:5000
```

## Credits

Thanks to Dan Patrick for the platform logo collection, the content-rating
authorities, the EmulatorJS and emulator-core contributors, and the Archivo,
IBM Plex, and Funnel font creators. See [Credits and Third-Party Notices](THIRD_PARTY_NOTICES.md)
for sources, individual credits, and component license texts.

## License

Copyright (C) 2026 Moonlark Studios LLC.

Ottercade's original source code and documentation are licensed under the
[GNU Affero General Public License, version 3 only](LICENSE).

That license does not grant rights to bundled third-party software, fonts,
platform logos, content-rating marks, other trademarks, or third-party
elements reproduced in test goldens. See [Third-Party Notices](THIRD_PARTY_NOTICES.md)
and [Trademarks](TRADEMARKS.md) for the applicable boundaries and notices.
