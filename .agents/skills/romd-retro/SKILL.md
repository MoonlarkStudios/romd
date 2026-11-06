---
name: romd-retro
description: Use after ROMD validation failures, environment-specific tool issues, flaky tests, or completed agent runs that reveal durable facts future agents should know; updates docs/development/known-issues.md only for newly confirmed known issues.
---

# ROMD Retro

Use this skill to convert durable validation facts into reusable guidance.

## Decision Rule

Search the existing issue headings and matching command/error in
`docs/development/known-issues.md` and `docs/development/known-issues-archive.md` before adding an
entry. Read only the relevant sections; fixed historical entries are evidence,
not a reason to accept a new failure. If an archived FIXED issue recurs, move
it back to `docs/development/known-issues.md` and update its `Status:` line rather than
adding a duplicate.

When an entry becomes FIXED, move the whole section verbatim to the end of
`docs/development/known-issues-archive.md` and repoint any inbound `known-issues.md#anchor`
links.

Append to `docs/development/known-issues.md` only when the issue is:

- reproducible or confirmed from command output,
- environment/tooling/test related rather than a product bug being fixed now,
- likely to recur for future agents,
- not already covered by an existing section.

If the issue is a real regression in the touched surface, fix it instead of
documenting it as known debt.

Route a changed command to its task/runbook and a changed invariant to its
scoped instructions. Do not add a known-issue entry for ordinary documentation
drift or copy a task transcript into permanent guidance.

## Known-Issue Section Format

Append one section per issue. Match the existing house style:

```markdown
## Short issue title

Status: FIXED / MITIGATED / DEFERRED

Command: `command that reproduces it`

Root cause: concise diagnosis.

Change: mitigation already made, or `None`.

Agent guidance: what future agents should do, avoid, or report.
```

Use `Commands:` with bullets when more than one command is involved. Preserve
existing sections and do not rewrite unrelated guidance.

## Retro Summary

When no docs change is warranted, say why. When docs change is warranted, report
the new section title and the evidence command output came from.
