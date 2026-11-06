# Web development

Complete the [development environment setup](../../docs/development/getting-started.md) first. For the
integrated stack run `mise run dev` from the root; use `dev:containers` for a
stable containerized backend. These commands configure the portals' proxies and
origins, including linked-worktree ports reported by `mise run dev:status`.

For manual frontend startup, run the following from `web/`. The example uses
ordinary-checkout source API ports; set API origins and browser ports explicitly
when connecting to another stack. Avoid starting duplicate Vite servers on a
managed session's ports. `mise exec` selects the pinned toolchain without shell
activation:

```bash
mise exec -- pnpm install --frozen-lockfile
mise exec -- pnpm dev
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:5002 mise exec -- pnpm dev:consumer
mise exec -- pnpm dev:player
```

Run each development command in its own terminal. The admin portal listens on
`http://localhost:5137`, the consumer portal on `http://localhost:5174`, and the
isolated player on `http://localhost:5175`. Use the
[split-host runbook](../../docs/architecture/split-host-topology.md) for worker-first provisioning,
backend startup, and authentication configuration. The commands above prepare
the frontend from a clean checkout; sign-in and data still require its API.
The player requires its configured allowed portal origin and runtime assets
before it can start a game; see [browser playback](../../docs/architecture/consumer-delivery.md).

Each command compiles its workspace dependencies in dependency order before
starting Vite. This includes the appropriate API client and shared presentation
packages. Compilation uses checked-in generated TypeScript; it does not require
a running API or regenerate schemas. Dependency compilers then stay in watch
mode so edits to shared TypeScript reach the consuming app through Vite.
Shared CSS is served directly from source. Stop the command with Ctrl-C to
stop its compiler watchers and Vite together. A failed initial compilation
blocks startup; a compiler process exit shuts down its app as well.

After backend contract changes, run `pnpm api:update` to regenerate schemas and
API clients following the [API client workflow](../../.agents/skills/romd-web-api-client/SKILL.md).
For web changes, run `pnpm lint`, `pnpm test`, and `pnpm build`. The test command
also checks development startup ordering, failure handling, and shutdown.

## Browser save regression

The opt-in source runner uses the production portal/player bridges, pinned
EmulatorJS CDN assets, an original synthetic battery-backed NES ROM and a
disposable browser profile. It checks two concurrent accounts, save/state
namespaces, explicit older-save recovery, exact backup downloads, protection of
existing progress and a browser-process restart. It needs loopback sockets,
outbound CDN access and Playwright's Chromium. From the repository root:

```sh
mise exec -- pnpm --dir web test:prepare
mise exec -- npm ci --prefix scripts/release/browser
mise exec -- npm --prefix scripts/release/browser exec -- playwright install chromium
mise exec -- node scripts/release/browser/save-isolation.mjs
```

To use an already installed Chrome, set `ROMD_TEST_BROWSER_CHANNEL=chrome` for
the final command. The runner owns its ephemeral ports and temporary profile
and removes them on exit. It does not use personal browser data or a running
ROMD server. This source-level check does not qualify a downloaded release or
replace authenticated packaged-browser qualification.
