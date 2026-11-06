# AGENTS.md - ROMD Backend Tests

Read [backend guidance](../src/AGENTS.md) before editing tests here. This
directory is a sibling of `src/`, so its backend instructions are not part of
this directory's ancestor chain.

Follow the [backend standards](../docs/development/dotnet-development.md#examples-and-verification).
Test observable behavior, rejected transitions and invariant preservation.
Pure-policy tests should need no database or mock graph; choose table cases or
property-based tests when they expose relevant risks such as round trips,
normalization, permutations or state transitions. No new test library is
required by this preference. Use real PostgreSQL tests for locking, constraints,
atomic commits and recovery. Check collection ownership and child mutation
boundaries when changing aggregates. Architecture checks protect meaningful
dependencies and capabilities, not incidental internal structure.

Use the root validation routing. PostgreSQL infrastructure tests need the container engine configured through
[development setup](../docs/development/getting-started.md);
host behavior and integration fixtures also need `mise run test:integration`.
When adding a test project, register it in `scripts/validation/checks.json`
(group `backend`, `integration` or `mount-dev`, with a required CI route) in the
same change and run `mise run agents:check` to verify routing. `Romd.PostgreSql.TestSupport`
is shared fixture code, not an executable test suite.

## Backend test conventions

- xUnit + Shouldly assertions + NSubstitute mocks.
- The pinned xUnit framework is v2: test-class async cleanup must implement
  `IAsyncLifetime` (`Task InitializeAsync` and `Task DisposeAsync`). Standalone
  `IAsyncDisposable` is ignored by this framework. Helpers explicitly owned by
  `await using` may use `IAsyncDisposable`. Constructor/field database fixtures
  are created per test case; dispose their contexts before their owned database.
- Test names: `MethodName_Scenario_ExpectedBehavior`.
- Use Arrange / Act / Assert structure.
- Integration tests use `IntegrationTestCollection` shared fixture for Hangfire
  state.
- NSubstitute cannot cleanly mock `IAsyncEnumerable`; use `FakeMetadataProvider`
  in `tests/Romd.Application.Tests/Titles/Enrichment/Helpers/` (enrichment
  policy) or `tests/Romd.Infrastructure.Tests/Enrichment/Helpers/` (adapters).
- Pre-existing noise: backend builds may emit MSB3492 cache warnings. Do not
  fix that unless the touched change made it new.
- A full-suite integration failure with `Cannot access a disposed object ...
  'IServiceProvider'` is the `JobActivator.Current` leak, not noise. Follow the
  matching entry in `docs/development/known-issues.md`.
