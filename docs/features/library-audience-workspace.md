# Audience libraries

A library describes an audience: the games a person can access, their rating
policy and the collections they see. Each person has one assigned library,
wherever they play. Collections organize games; they grant no additional access.
Start with [Your first library](first-library.md) for a complete walkthrough.

## Create or open a library

Use **Libraries → New library**, choose a name, initial selection and maximum
content-rating age. The workspace has **Overview**, **Games**, **Collections**,
**People** and **Preview** tabs. Overview counts releases: regional versions
and revisions can make that count larger than the distinct game count.

## Choose games and ratings

In **Games**, rules and results sit side by side on wide screens; use the
**Rules / Results** switch on smaller screens.

Start with **Content ratings**: set the maximum age and **When a game has no
rating**. The default, **Flag for review**, keeps unrated games hidden. The
strictest available rating board is used unless you choose another basis under
**Rating sources and exceptions**. Imported files do not establish a reviewed
age rating. Ratings apply to handpicked games and attached collections too.

**Select with rules** builds an audience from systems and genres. Explicit
inclusions bypass genre rules, while tracking, ratings and system scope still
apply. Explicit exclusions stay excluded. **Handpick games** starts from an
explicit list; an empty list creates an empty library under the same restrictions.

For original multi-disc games, **Disc sets** chooses accepted sets. Every member
still obeys the audience rules. Save and refresh to publish the chosen membership;
repair unresolved selections instead of assuming they grant access.

## Tracking and ownership

New libraries consider tracked titles. Track a title from the catalog when you
want it in your collection. Enable **Include untracked titles** only to consider
the wider catalog under the other rules; handpicked games obey tracking too.

Tracking and ownership are separate. A tracked game may have no files or fail
another audience rule. **Include missing games** permits eligible catalog entries
without downloadable files. Untracking deletes neither stored ROMs nor catalog
metadata. The **Untracked** exclusion reason identifies the tracking gate.
Pin-only changes do not change tracking membership; libraries including untracked
titles are not narrowed by that gate.

## Inspect the draft

**See what changes** compares saved and draft rules against the same current
catalog snapshot. Inspect matching, added, removed or excluded games, or search
for a specific title. Results explain the primary exclusion and relevant ratings.
Evaluation saves no rules, starts no publication and changes no consumer access.

Each result page evaluates again, so concurrent catalog changes can alter totals
between pages. This compares rules against the current catalog, rather than
against the older published audience or a device's emulator compatibility.
Choose **Save changes** when the draft is right. Unsaved edits require a decision
before leaving the workspace.

## Publish changes

**Save changes** records configuration; **Refresh library** publishes it. A new
library needs its first successful refresh before consumers can use it. Imports,
policy edits and tracking changes mark unpublished changes without scheduling an
automatic refresh job. Existing consumers keep the previous published snapshot,
including after narrower ratings, exclusions or untracking, until you refresh.

If refresh fails, inspect **Jobs**. Let concurrent catalog/rule changes settle and
refresh again. Invalid configuration needs repair; a failed job does not prove
that intended restrictions were published. There is no automatic daily publication.

## Arrange collections

Use **Collections** to attach, detach, reorder and feature shared collections.
Attachment controls placement for this audience. Editing a shared collection
changes it for all attached libraries. Empty filtered collections remain manageable
here and are omitted from consumer shelves. See [Collections](collection-workspace.md).

## Assign people and preview

In **People**, choose **Assign a person**, or **Manage people and roles** to
create an account. Reassignment changes the person's audience.

**Preview** shows the saved, owned games in the published audience, including
collection order. Inspect it before inviting people to the consumer portal.
Preview does not simulate a device or promise emulator compatibility.

Contributors: [library publication](../architecture/library-publication.md) owns
snapshots, counts and concurrency. Tracking membership and affected library
revisions change atomically in PostgreSQL; failed writes leave both unchanged.
See the [database boundary](../self-hosting/configuration.md#database-and-installation-boundary)
for the current fresh baseline.
