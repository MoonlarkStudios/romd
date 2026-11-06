# Agentic Development In ROMD

The default workflow is one coding agent (Codex or Claude Code) taking a bounded
task through source changes, verification, and a reviewable diff. Use the
repository's existing toolchain and executable checks. Add instructions when
they capture a ROMD constraint that an agent cannot infer cheaply from code.

## Start A Task

1. Describe the desired behavior and observable acceptance criteria. For a bug,
   include the trigger and expected result; for a review, name the diff or area.
2. Inspect the working tree and read root plus relevant scoped `AGENTS.md`
   files. Start from [the documentation index](../README.md) when a runbook or
   architecture decision is needed. Backend work also follows the
   [.NET development standards](dotnet-development.md), which distinguish
   required invariants, preferred defaults, and techniques selected for the task.
3. Identify persisted contracts, generated artifacts, host boundaries, and the
   smallest meaningful verification route. Use `romd-plan-change` for changes
   that span surfaces or affect migration/validation policy.
4. Implement a coherent change and verify the affected behavior. Review the
   final diff for accidental edits, weak assertions, and missing migration or
   generated-contract updates.
5. Report the result, evidence, and remaining limitations. Update the owning
   documentation when a supported workflow changes.

For a routine fix, these decisions can be brief. A design plan or persistent
handoff is useful when the work spans sessions or depends on unresolved
choices; it is not required for every edit.

Example request:

```text
Fix <observable failure> in <surface>. Preserve <contract>. Acceptance:
<observable result>. Use the scoped instructions, validate the affected
behavior, and report any checks you could not complete.
```

## Setup And Validation

[Development setup](getting-started.md) owns the toolchain, engine selection,
`mise run bootstrap`/`doctor`, the source and container stacks, ports,
`dev:status`, shutdown, validation evidence and `dev:smoke` qualification.
[Development demo data](demo-data.md) owns the `.demo/` workspace,
including copying it into a worktree and the one-terminal `--wait` flow. The
[root validation table](../../AGENTS.md) selects checks; `scripts/validation/checks.json`,
`.mise.toml`, `web/package.json` and the console `.mise.toml` implement them.

Constraints specific to agents:

- Do not start a whole development stack when a focused build or registered
  check (`python3 scripts/validation/run.py --id <check>`) is sufficient.
- Use one worktree per concurrent task, with its own ports, database and data.
  Discover URLs, state and logs with `mise run dev:status`; never scrape logs,
  copy another checkout's data settings or credentials, or stop processes you
  did not start. Never use global `pkill dotnet`, `db:down`, resets or volume
  pruning as task cleanup.
- Commit development-runner or demo-tooling changes before `mise run dev:smoke`;
  it tests committed HEAD and retains private evidence.
- Never commit private content, inventory, credentials, rendered configuration
  or raw evidence, and do not use `.demo/` as durable application storage.
- GitHub Actions runs the [CI workflow](../../.github/workflows/ci.yml) on every
  pull request and on `main`, and its required checks gate merges. Each CI step
  maps to a registered local route or an allowlisted CI-only reason, so
  reproduce a hosted failure locally before changing code. Cite the hosted run
  with each PR, keep local evidence for anything CI does not cover (hardware,
  packaged qualification, opt-in suites), and do not rerun failed jobs without
  a diagnosis or weaken workflows to manufacture a pass.

Evidence should identify the command, working directory, result, and scope.
Registered checks write `results.json` under `.artifacts/validation/`; commit
before the run you cite, or state that it covered uncommitted changes. A filtered
test pass establishes the filtered behavior; mention that the full suite was not
run. For a failure, retain the actionable error, map any matching known issue,
and distinguish an environment block from a regression. Do not infer a pass from
a prior report or a successful retry alone. Local `test:all` covers backend
suites only; native packaged-client builds and actual emulator playback require
separate qualification.

Use `mise run agents:check` (Python 3.11+; prepares an isolated, hash-locked
YAML parser) after documentation or agent-infrastructure changes. It checks local
Markdown links, anchors and HTML assets, TOML syntax, required agent fields and unsupported session-level settings
in custom-agent files, backend test-project routing
through the validation registry, CI-to-registry parity, and the development
contract tests. It also checks decision-record structure and that code
citations name existing records and headings. It does not validate all Codex
config keys, skill semantics, external link availability, or product behavior.
Keep `TMPDIR` outside every Git worktree. The publication report-contract tests write evidence under it and otherwise fail
with `Evidence must be outside the source checkout` or `Evidence parent must be
outside every Git worktree`; both refusals are intended.

## Review And Handoff

Keep coherent changes on a topic branch. When asked to commit and open a PR,
record meaningful commits, push the branch, and include the changed behavior,
validation results and remaining limits in its description. For stacked PRs,
name the dependency and base each follow-up on the preceding branch so reviewers
see only the new change. Leave approval and merge to the maintainer unless the
user explicitly authorizes them. Do not enable automatic merge by inference.

## Instruction Ownership

| Artifact | Keep here |
| --- | --- |
| Root `AGENTS.md` | Shared constraints, surface routing, validation selection |
| Scoped `AGENTS.md` | Local invariants and conventions that change implementation decisions |
| `CLAUDE.md` | Thin Claude Code bridge that imports root `AGENTS.md` |
| `.agents/skills/*/SKILL.md` | Conditional, repeatable workflows and non-obvious steps |
| `.codex/agents/*.toml` | Optional role purpose, boundaries, and evidence requirements |
| Current runbooks and decisions | Supported procedures and architectural rationale |
| `docs/development/known-issues.md` | Open, deferred and mitigated tooling/environment problems |
| `docs/development/known-issues-archive.md` | Fixed issues kept for regression diagnosis |
| Reports and handoffs | Dated evidence or continuation context with explicit limits |

Keep `AGENTS.md` focused on constraints and task routing. Load the owning guide's
relevant sections and governing decisions for the behavior being changed; expand
when another contract is affected. Do not recursively preload every linked doc,
the whole issue history or every skill. Search large references by task/error;
load a skill's body when its trigger applies. When trimming instructions, keep
mandatory invariants, exceptions and validation routes, and link their owner
instead of copying procedures. Measure startup instruction bytes separately
from references loaded on demand; a byte target never justifies dropping a rule.

## Agent Discovery

Both Codex and Claude Code are supported. Neither loads every scoped file
automatically from a root-started task, so root `AGENTS.md` routing tells the
agent which scoped `AGENTS.md` and skill files to open.

- **Codex** builds its project instruction chain from `AGENTS.md` files along
  the starting directory's ancestors. Repository skills live in
  `.agents/skills/`; their descriptions select a workflow before its full body
  is loaded. See official [instruction discovery](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
  and [skill discovery](https://learn.chatgpt.com/docs/build-skills).
- **Claude Code** loads `CLAUDE.md`, which imports root `AGENTS.md`. It does not
  read nested `AGENTS.md` files on its own and does not discover `.agents/skills/`
  as Claude skills. Open the scoped `AGENTS.md` and `SKILL.md` files that root
  routing names before editing that surface. See official
  [memory](https://docs.anthropic.com/en/docs/claude-code/memory) documentation.

## Optional Delegation

Delegate only when explicitly requested. Use `romd-scout` for a bounded source
question, `romd-implementer` for an owned change, `romd-reviewer` for correctness
and validation scrutiny, and `romd-librarian` for documentation maintenance.
Avoid overlapping edits and shared generated-output ownership. Each agent must
preserve others' work; the primary agent owns integration and final verification.
Delegated agents do not delegate again unless the user asks for that structure.

Codex discovers these roles from standalone TOMLs in `.codex/agents/`; the
project config caps concurrent subagents. Other agents can apply the same role
descriptions when the user requests delegation. Keep models, account settings,
providers, and permission preferences in personal configuration. Custom agents
inherit the parent session's permissions; `sandbox_mode`, `approval_policy`,
`model_provider`, and `mcp_servers` in a custom-agent file do not override them.
Read-only role instructions prohibit edits as a behavioral constraint, not an
enforced filesystem boundary. Use session-level permissions when that boundary
must be enforced. See official
[custom agents and inheritance](https://learn.chatgpt.com/docs/agent-configuration/subagents).

## Improve From Evidence

When a task exposes recurring friction, fix the owning command or invariant
first. Use `romd-retro` only for a new durable environment fact. Evaluate workflow
changes against representative tasks with explicit acceptance evidence:

| Task | Evidence to assess |
| --- | --- |
| Backend behavior fix | Reproduction fails before the fix; behavior passes after it; the diff preserves domain invariants and transaction ownership. |
| Consumer API contract change | Backend, OpenAPI, generated web client and handwritten console call sites agree; web, console and applicable backend checks are reported. |
| Console migration or runtime change | Persisted keys and compatibility decisions are identified; migration/runtime checks cover the changed behavior. |
| Environment-blocked run | The actual failing command and cause are recorded, matching known issues are consulted, and unrun checks remain explicit. |

Keep dated results in the pull request. Compare correctness, validation
completion, repeated failures, and human interventions against the previous
workflow. Structural checks establish routing and configuration constraints;
these task outcomes establish whether the guidance helps implementation. A new
role, hook, or skill should solve an observed gap.
