# Console theme token governance

The console has one runtime `ThemeData` seam. A skin maps private primitives
into `ColorScheme`, `TextTheme`, Material component themes, and five focused
`ThemeExtension`s. Widgets read the active mapping from `BuildContext`; they do
not import primitive palette or design-token globals.

Large procedural artwork contracts stay nested (`ConsoleAmbient*Spec` and
`ConsoleCoverArtworkSpec`). They are counted and classified by governance, but
they do not expand the flat `ThemeExtension` vocabulary with painter-specific
fields. Ambient palettes use named `ConsoleAmbientPalette` roles rather than
positional color lists, and artwork definitions are structurally validated
before `RomdSkins.compose` will produce a theme.

The seam is not permission to move every widget literal into one global file.
Every extension field is classified by
[`tool/theme_token_registry.tsv`](../tool/theme_token_registry.tsv), and CI
requires every field to match exactly one category with a rationale.

## Categories

- **Semantic** tokens describe a role shared across the product: `textMuted`,
  `panelSurface`, `spacing.lg`, `panelRadius`, `focus`, or `dialog` elevation.
  Widgets choose the role; skins choose its value.
- **Component** tokens describe a reusable visual component contract, not a
  route or screenshot: navigation dock, content rail, keycap, hint bar, focus
  ring, session cluster, settings surface, or status panel.
- **Unique** tokens are rare, reviewed art-direction specifications that cannot
  honestly be generalized. Current exceptions are the attract-to-profile ROMD
  brand choreography and procedural ambient/avatar/cover artwork.
- **Structural constants are not theme tokens.** Content arrangement, maximum
  width chosen for a particular workflow, focus order, carousel topology, and
  screen-specific positioning stay next to the widget. A skin may change visual
  density and component metrics; it does not redesign information architecture.
  Current examples include the login/form content-width constraints, the fixed
  profile carousel composition, controller-diagram coordinates, detail action
  widths that determine wrapping, media aspect ratios, and paging thresholds.

## Admission rule

A new screen-prefixed field such as `login*`, `home*`, or `emulator*` is rejected
unless the registry explicitly classifies it and explains why no semantic or
reusable component role fits. Prefer, in order:

1. `ColorScheme`, `TextTheme`, or a Material component theme.
2. An existing global semantic token.
3. An existing shared component token.
4. A new shared component role named for the component responsibility.
5. A reviewed unique specification with a written rationale.

Do not add a token solely to preserve a one-off literal. If convergence changes
the baseline visual, review the change intentionally and update goldens in the
same commit.

`tool/check_widget_style_literals.sh` rejects raw colors, typography, shape,
elevation, interaction geometry, and inline derived color intensities across
all of `lib/src`. Its only renderer exemptions are alpha-zero ambient gradient
endpoints and the persisted profile-avatar identity artwork.

`tool/check_theme_token_usage.sh` rejects declared fields with no consumer
outside their definition. `tool/check_theme_extension_contracts.sh` rejects a
field omitted from `copyWith` or `lerp`; `ConsoleArtworkTheme` is the one
documented whole-object midpoint snap. Together with the registry, these checks
mean a token must explain why it exists, be consumed, and remain runtime-safe.

## Typography ontology

`TextTheme` is the single owner of typeface, size, weight, height, and tracking.
Its five Flutter families remain internally ordered (`Large >= Medium >= Small`)
and widgets select meaning through semantic aliases in
`console_theme_context.dart`: `hero`, `pageHeading`, `sectionHeading`,
`cardTitle`, `chromeStatus`, `supportingTitle`, `utilityLabel`, `sectionLabel`,
`metadataStrong`, `body`, `bodyCompact`, `metadata`, `action`, `chipLabel`, and
`eyebrow`. These aliases are projections of `TextTheme`, not a parallel style
store. Semantic state color is applied separately; no type role bakes in accent
meaning.

Widgets must not synthesize a new role by borrowing size, weight, or height from
unrelated roles. A recurring composition graduates to a semantic alias. Truly
unique display typography, such as the device verification code and ROMD entry
lockup, belongs to the reviewed artwork specification.

Foreground emphasis uses four ordered roles (`textStrong`, `textBody`,
`textMuted`, `textFaint`) plus the state-specific `textDisabled`. Disabled text
is not a fifth general-purpose emphasis step and must not carry essential
information.

## Skin composition and future custom themes

`RomdSkins.compose` assembles independent palette/type, density, elevation,
motion-character, and artwork axes. Density and motion are curated selections;
a light palette does not copy or redefine the full metric system. All axes
resolve to Flutter-native `ThemeData`, `ColorScheme`, `TextTheme`, Material
component themes, and the five extensions.

A future external theme file must not serialize Flutter objects. The supported
boundary is a versioned primitives-and-selections document that is parsed,
migrated, contrast-checked, and converted into the required arguments for
`RomdSkins.compose`. Curves, shadows, `TextStyle`, `Alignment`, `RangeValues`,
renderer geometry, and arbitrary font/assets stay behind curated identifiers or
first-party presets. Invalid definitions resolve to the last known-good theme;
they are never partially applied.

## Runtime contract

Ordinary widgets read theme values in `build`. Stateful objects that copy theme
values into painters or animation controllers refresh them in
`didChangeDependencies`. Every migrated custom painter/controller surface needs
a mounted runtime-switch test; per-skin goldens protect reviewed baseline
visuals for both production skins.

Profile accent ARGB values are user content, not skin tokens. They remain stable
across skins and must be paired with contrast-safe derived foregrounds. The
selectable values and foreground calculation live in `ProfileIdentityPalette`,
outside the skin factory; widgets must not reuse them for general focus or
control styling. The chosen avatar topology is identity content too; the active
skin supplies only its lighting, rim, and surrounding focus treatment.

Two production skins ship: `RomdSkins.baselineDark()` ("the archive at night")
and `RomdSkins.baselineLight()` ("the archive in daylight"). Both share the
baseline density and motion axes, register in the contrast and contract suites,
and own per-skin golden baselines. Key art and media stay dark-backed in every
skin, so cover backdrop shading does not vary by brightness.
`RomdSkins.seamTestTheme()` is intentionally garish and exists to prove runtime
coverage; it is a seam fixture, never a product skin.
