# Admin workspace styles

Libraries is the visual reference for the audience and curation workspaces.
Collections and Libraries share the rules in
`web/packages/romd-admin-app/src/components/Workspace/Workspace.module.css`.
Page-specific CSS composes these classes instead of copying their values.

## Shared hierarchy

- `page`: stable 1440px maximum width across tabs, with responsive page padding.
- `hero`, `heading`: compact page headers with a fixed 26px title, zero letter
  spacing, and primary action alongside. Do not add decorative eyebrows or
  introductory marketing copy. Show actual descriptions only when supplied.
  The shared heading reserves a 36px minimum row (scaled with Mantine) and
  vertically centers its text to match standard `sm` actions. Use `hero` even
  for title-only headers; keep the shared heading style rather than introducing
  a page-specific line height. Headers may grow for wrapped titles or actions. The shared `hero` owns a
  16px gap below the heading and subtracts an enclosing Mantine Stack gap to
  avoid doubled spacing. Do not add page-specific header margins. Tabs use
  compact 8px vertical padding across workspaces.
- `CurationHelp`: on-demand vocabulary and relationship definitions beside
  Collection and Library titles. Keep this guidance out of the permanent header.
- `sectionHeading`: use with the appropriate semantic heading level; hierarchy
  should not depend on Mantine's default font size.
- `panel`: shared surface, border, radius, and responsive inset.
- `tabs`, `tabsList`, `tab`: common spacing and weight, with horizontal scrolling
  when needed. Pass list/tab classes through Mantine's `classNames` prop.

## Actions

Use `workspaceActionProps` from `components/Workspace/workspaceActions.ts` for
themed workspace actions. It supplies the shared color and small radius. Use
filled for the primary save/create action, light for secondary actions, and
subtle for low-emphasis links. Actions use the theme's `sm` default unless a
compact layout requires otherwise.
Destructive actions remain red. Utility icon actions remain subtle gray.

Back navigation uses a subtle gray button, zero padding, and `IconArrowLeft`
at size 15. Relationship removal uses `IconUnlink` at size 17 with an accessible
`Detach …` label, from either side of the relationship.

## Adding or refining a workspace

Start from these shared rules. Keep content-specific layouts (catalog/results
columns, covers, audience counts) local. If a shared visual decision changes,
update the shared source and review both Libraries and Collections at desktop
and mobile widths, in light and dark mode. Do not introduce local title scales,
button radii, or tab spacing for the same hierarchy.

The global Mantine theme still owns the font family and basic control defaults.
These workspace conventions are scoped to the redesigned audience and curation
pages; they do not change unrelated Admin views globally.

## Catalog search

Keep title search and system scope visible. Secondary filters expand inline
beneath them; apply immediately to the catalog list/grid with removable chips.
Do not put filtered results or release review in a drawer. Release review must
use the same completeness and preference policy as the tracked collection and
intersect with the other predicates before pagination.

Persist search, filters, sort, and tracked/all scope in URL parameters with
history replacement. Opening title details uses normal navigation so Back
restores the search; panel expansion and selection remain transient UI state.
Legacy `view=missing` and `view=upgrades` URLs normalize to the tracked view plus
`releaseReview`. The catalog count endpoint runs independently of the results request, using the same filters. Cache its exact total while appending cursor pages; show counting and retry states without blocking results.
The total reflects count-query time, not a transactional snapshot across subsequent requests.

## Mantine compatibility

Application providers use the foundation's `portalCssVariablesResolver`, built
on `v8CssVariablesResolver`, and the scoped consumer presentation uses the v8
resolver directly to preserve translucent light-variant
colors during migration. Keep test providers aligned with this behavior; removing
the resolver is a separate visual change requiring admin and consumer review.
