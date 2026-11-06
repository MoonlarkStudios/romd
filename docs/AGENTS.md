# AGENTS.md - ROMD Documentation

Use [the documentation index](README.md) to find the relevant owner and current
runbook. Paths in commands are relative to the stated working directory.

- Place documents by audience: `self-hosting/` (operators), `features/` (admin
  behavior), `development/` (contributors), `architecture/` (current design and
  invariants), `maintainers/` (release and repository operations),
  `decisions/` (decision records in force) and `design/` (feature
  designs/proposals).
  Surface-specific docs live with their surface: `web/docs/`,
  `clients/romd_console/docs/`, `native/libchdr/`.
- Keep `self-hosting/production-deployment.md` and
  `self-hosting/media-processing.md` siblings; `scripts/deployment/bundle.sh`
  ships them with the configuration reference in the deployment bundle.
  Packaging keeps included links local and pins omitted source references to the
  release source commit.
- Verify commands against `.mise.toml`, `scripts/validation/checks.json`,
  package scripts, and CI. State tool or environment prerequisites and
  distinguish local checks from release gates.
- Keep `AGENTS.md` for short constraints and routing, skills for conditional
  workflows, and runbooks for operational procedures. Link to the owner instead
  of copying a procedure into multiple instruction files;
  `development/getting-started.md` and `development/demo-data.md` own
  development and demo procedures.
- Distinguish current behavior, accepted decisions, proposals, and dated
  evidence. A recorded result does not prove the current checkout works.
- Write and change decision records, including console ones, by
  [the decision-record guide](decisions/README.md): normative rules with
  reasons, edited in place, with no status, plans or current-state inventory.
- Keep media/platform/client qualification results in `architecture/media-status.md`.
  Operational guides retain commands, prerequisites and recovery procedures and
  link to that matrix instead of copying dated results.
- Update the affected current doc when behavior changes. When a plan lands,
  fold lasting rules into the owning guide or decision and delete the plan.
  Keep raw diagnostics, dated receipts, and agent handoffs out of Git: put
  validation evidence in the pull request and update the maintained doc's
  conclusion instead.
- Search `development/known-issues.md`, then
  `development/known-issues-archive.md` (fixed entries), by command/error.
  Append only newly confirmed, recurring tooling/environment issues using the
  `romd-retro` skill; a product regression belongs in the fix. Move an entry to
  the archive verbatim once its status becomes FIXED.
- Run `mise run agents:check` after documentation changes; it checks local Markdown
  links/anchors and HTML assets across the tracked/non-ignored inventory. For
  command changes, verify that the task exists and run it when meaningful and
  feasible; syntax/link checks alone do not verify runtime behavior.
