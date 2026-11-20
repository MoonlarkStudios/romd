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
restores the search, scroll position, and preview. Catalog stores the preview ID
in `title`. Bulk selection stays local and is cleared when filters change.
Legacy `view=missing` and `view=upgrades` URLs normalize to the tracked view plus
`releaseReview`. The catalog count endpoint runs independently of the results request, using the same filters. Cache its exact total while appending cursor pages; show counting and retry states without blocking results.
The total reflects count-query time, not a transactional snapshot across subsequent requests.

## Mantine compatibility

The Admin app and its shared test renderer use the foundation's
`adminCssVariablesResolver`. The consumer portal uses
`portalCssVariablesResolver`, and the scoped consumer presentation uses
`darkCssVariablesResolver`. Each resolver extends Mantine’s compatibility resolver to preserve variants.
Admin loads the foundation variables and typography at startup so workspace
routes share the same motion and text roles. Keep test providers aligned with
their app’s resolver.

## Inspectors and sections

- Use `InspectorLayout`, `InspectorPanel` and `useInspectorSelection` for a
  directory that previews one item at a time. Measure the workspace after the
  navigation and gutters. Closed inspectors leave the full width to the list.
  Motion uses foundation tokens and follows the user's reduced-motion setting.
  With 500 or more loaded Catalog rows, the column widths change once and
  only the preview animates.
- Keep the title or system name a real link. Expose the preview as a separate
  button, mark its row with `aria-current`, and reserve `data-selected` for
  checked rows. Close with Escape or the close button and return focus to the
  preview trigger. Up and Down move through the visible results. Use container
  queries to hide secondary columns as the results narrow.
- Use `SectionTabs` for addressable sections. Catalog views use the same
  navigation on `view`, and Title sections use path segments. An inspector in
  Catalog or Systems opens full details on a narrow workspace. Title section
  inspectors stack below the list, with focus moved into the opened inspector.
- Compose title identity and progress with `TitleIdentity`, `RequirementList`
  and `LinkedCountList`. Show Enrichment status as a collapsible summary above
  the active Title section.
  Use `SourceMarker` and `EditorFooter` for provenance and drafts. Register
  dirty editors with the Admin draft guard.
- Catalog's Needs review badge shows the global outstanding review count. The
  queue has its own search and state filter. Enter on a previewed row starts
  review, and Up or Down moves to another title. The row stays in place after
  resolving its last issue until the curator moves on. Use Jobs for run history,
  cancellation and retry.
