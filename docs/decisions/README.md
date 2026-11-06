# Decision records

A decision record states rules ROMD holds itself to and the reasons behind
them. Every record in this directory and in the
[console decisions](../../clients/romd_console/docs/decisions/) is in force.
Merging a record to `main` accepts it. Records have no proposed, implemented,
deprecated or superseded state: a proposal lives in its pull request or in
[`design/`](../design/), and a retired decision is deleted.

Records are normative. They describe the target state and stay true as
written however much code conforms. Gaps between the rules and the code
belong in issues, pull requests or an architecture status document such as
[media status](../architecture/media-status.md), never in the record.

The [API contract and compatibility policy](api-contract-policy.md) is the
model record.

## When to write a record

Write or change a record when a choice:

- is costly to reverse;
- constrains more than one module, host or surface;
- sets a contract: a wire format, persisted identity, host ownership, a
  security boundary or a storage format; or
- would otherwise be debated again.

Choices fully expressed by local code and tests need no record.

## One record per decision area

A record covers rules that change together and share the same forces. Split
a record when its parts change independently, belong to different surfaces,
or pass about 250 lines.

Title the record for the decision area as a noun phrase, such as "API
Contract And Compatibility Policy", never an event or a phase such as "Phase
0A Decisions" or "PostgreSQL Cutover". The file name is a short kebab-case
slug chosen when the record is created. The slug is the record's identity:
the title may change, but the file keeps its name. Console-only decisions
live in `clients/romd_console/docs/decisions/`.

## Changing a record

Change the record in place, in the same pull request as the code that adopts
the change when possible. The pull request description gives the trigger and
the evidence; Git history is the journal.

- **Refinement** (a sharper rule, a new case): edit the rule and its reason.
- **Reversal:** rewrite the rule, and add the abandoned approach to Rejected
  Alternatives with the reason it was abandoned. Readers without the history
  learn about a reversal only from that entry.
- **Retirement:** delete the record. Move any surviving rule to the record
  that now owns it, then update the index and every citation.

## Structure

```markdown
# <Decision area>

Related decisions: <links> (optional)

## Context
## Terms (optional)
## Decisions
### <Rule group>
## Trade-offs
## Rejected Alternatives
## Guardrails
```

A record has exactly these `##` sections, in this order. It carries no status
line, date, owner or change log.

### Context

State the forces: the constraints, quality goals and pressures that make the
rules necessary, in a few short paragraphs. Do not inventory the current
code, and do not describe an implementation as "currently", "today" or
"already" doing something.

### Decisions

- Write each rule as a present-tense statement of what is true: "The worker
  is the only schema migration owner."
- Use "must" and "never" only for emphasis; records do not use BCP 14
  capitals.
- Group rules under `###` headings that name the rule, such as "One Error
  Envelope". Code cites these headings, so treat them as an interface.
- Give each rule its reason when the reason is not obvious, as a "because"
  clause or a short "Why:" line. A rule without a reason is a policy in
  disguise.
- Name the types, routes, configuration keys and schema objects that form the
  contract. Never cite a source file by line number.
- Include code only when the signature is the contract.
- When a known condition would reopen a rule, state it inside the rule as a
  re-decision trigger.

### Trade-offs

List the lasting costs ROMD accepts by holding these rules, such as
"Self-hosting requires PostgreSQL and a coordinated backup set." Trade-offs
are consequences of the target state, not work items.

### Rejected Alternatives

List each option that was seriously considered, with "Rejected:" and the
reason. Do not add options to fill the section.

### Guardrails

Name the tests, gates and scans that enforce the rules. Name the check, not
its result. When a rule cannot be checked mechanically, say how review
enforces it.

## What stays out

| Content | Where it goes |
| --- | --- |
| Implementation status, gate and approval logs, review findings, test counts | The pull request |
| Current-state inventories, violation lists and source line citations | An issue, the pull request or a working plan in the ignored `tmp/` |
| Migration sequences, cutover steps, implementation order, follow-up and deferred work | An issue or a working plan; a durable operator procedure goes to a runbook |
| Worked examples, data flows and current design detail | [`architecture/`](../architecture/) |
| Undecided options and planned features | [`design/`](../design/) or an issue |
| Dated measurements and experiments | The pull request; conclusions go to the owning architecture document, and recurring tooling defects go to [known issues](../development/known-issues.md) |
| Validation commands for a change | The pull request |
| Qualification procedures | A runbook in [`maintainers/`](../maintainers/) or [`self-hosting/`](../self-hosting/) |

No heading names plan work. The check rejects any heading containing
"follow-up", "implementation", "deferred", "acceptance" or "roadmap".

## Citing a record

Code cites a whole record by its repository-relative path, or specific rules
by the path followed by one or more quoted headings:

```text
(docs/decisions/api-contract-policy.md, "Opaque Public Identity" and "Strict Validation")
```

A quoted heading matches a `##` or `###` heading's full text, or the text
before its first colon. Quote the heading after the path: a rule named only
in the surrounding prose cannot be checked. Records do not number their
rules, so never cite one by number. Renaming a cited heading updates every
citation in the same pull request.

## Checks

`mise run agents:check` checks that every record follows this structure,
that this index links every record, that record links resolve, and that every
code citation names an existing record and heading. Run the check after
adding, changing, renaming or deleting a record.

## Index

| Area | Records |
| --- | --- |
| API and hosts | [API contract and compatibility policy](api-contract-policy.md), [host plane ownership](plane-ownership.md), [admin use-case, transaction and domain-event boundaries](admin-use-case-transaction-event-boundaries.md) |
| Persistence and catalog | [PostgreSQL single provider](postgresql-single-provider.md), [neutral source identity](neutral-source-identity.md), [DAT subscriptions](dat-subscriptions.md) |
| Media and delivery | [Game media](game-media.md), [media pipeline](media-pipeline.md), [media delivery](media-delivery.md), [original disc sets](original-disc-sets.md), [consumer runtime boundary](consumer-runtime-boundary.md), [VFS delivery through WebDAV](vfs-webdav-connections.md) |
| Console | [Flutter client](../../clients/romd_console/docs/decisions/flutter-client.md), [profile access and offline grants](../../clients/romd_console/docs/decisions/profile-access-and-offline-grants.md), [controller identity](../../clients/romd_console/docs/decisions/controller-identity.md), [controller mapping ownership](../../clients/romd_console/docs/decisions/controller-mapping-ownership.md), [virtual controller routing](../../clients/romd_console/docs/decisions/virtual-controller-routing.md) |
