---
name: romd-retro
description: Use after ROMD validation failures, environment-specific tool issues, flaky tests, or completed agent runs that reveal durable facts future agents should know; updates docs/known-issues.md only for newly confirmed known issues.
---

# ROMD Retro

Use this skill to convert durable validation facts into reusable guidance.

## Decision Rule

Append to `docs/known-issues.md` only when the issue is:

- reproducible or confirmed from command output,
- environment/tooling/test related rather than a product bug being fixed now,
- likely to recur for future agents,
- not already covered by an existing section.

If the issue is a real regression in the touched surface, fix it instead of
documenting it as known debt.

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
