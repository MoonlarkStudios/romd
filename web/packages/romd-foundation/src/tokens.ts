/**
 * ROMD design tokens — TypeScript mirror of tokens.css.
 *
 * For contexts where CSS custom properties cannot reach: inline styles,
 * canvas rendering, imperative DOM work. Keep in sync with tokens.css.
 */

export const romdColors = {
  accent: '#88C9B9',
  accentHover: '#9ED5C7',
  accentSoft: 'rgba(136, 201, 185, 0.16)',
  accentLine: 'rgba(136, 201, 185, 0.38)',
  accentGlow: 'rgba(136, 201, 185, 0.22)',
  catalogAccent: '#E6C074',
  warning: '#F09A90',
  sea: '#86B6C5',
  violet: '#AAA2CC',
  onAccent: '#191b1c',

  bg: '#191b1c',
  surface: '#222628',
  surfaceRaised: '#2B3032',
  surfaceHigh: '#343C3D',
  surfaceHighest: '#3C4443',
  surfaceDeep: '#141718',
  surfaceDeepest: '#101213',

  panelMuted: 'rgba(34, 38, 40, 0.48)',
  panel: 'rgba(34, 38, 40, 0.64)',
  panelStrong: 'rgba(34, 38, 40, 0.74)',
  mediaChip: 'rgba(25, 27, 28, 0.75)',

  borderSubtle: 'rgba(173, 183, 182, 0.14)',
  border: 'rgba(173, 183, 182, 0.22)',
  borderStrong: 'rgba(173, 183, 182, 0.34)',

  textStrong: '#F4EFE4',
  textBody: '#F4EFE4',
  textMuted: '#C9D1CD',
  textFaint: '#ADB7B6',
  textDisabled: '#788782',
} as const;

export const romdFonts = {
  display:
    "'Archivo', system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif",
  mono: "'IBM Plex Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
} as const;

export const romdMotion = {
  durationMs: {
    focus: 150,
    enter: 220,
    screen: 280,
    chrome: 360,
    intro: 480,
  },
  easing: {
    standard: 'cubic-bezier(0, 0, 0.2, 1)',
    emphasized: 'cubic-bezier(0.215, 0.61, 0.355, 1)',
    spatial: 'cubic-bezier(0.645, 0.045, 0.355, 1)',
  },
} as const;

export const romdLayout = {
  headerHeight: 64,
  headerPadding: 16,
  headerControlGap: 8,
  headerControlSize: 34,
  contentMaxWidth: 1440,
  pageGap: 40,
  sectionGap: 24,
  posterAspectRatio: '2 / 3',
} as const;

/**
 * CSS variable references, for inline styles that should track the
 * stylesheet layer (e.g. values that a future light scheme will re-theme).
 */
export const romdVars = {
  accent: 'var(--romd-accent)',
  accentHover: 'var(--romd-accent-hover)',
  accentSoft: 'var(--romd-accent-soft)',
  accentLine: 'var(--romd-accent-line)',
  accentGlow: 'var(--romd-accent-glow)',
  catalogAccent: 'var(--romd-catalog-accent)',
  warning: 'var(--romd-warning)',
  sea: 'var(--romd-sea)',
  violet: 'var(--romd-violet)',
  onAccent: 'var(--romd-on-accent)',
  bg: 'var(--romd-bg)',
  surface: 'var(--romd-surface)',
  surfaceRaised: 'var(--romd-surface-raised)',
  surfaceHigh: 'var(--romd-surface-high)',
  surfaceHighest: 'var(--romd-surface-highest)',
  surfaceDeep: 'var(--romd-surface-deep)',
  surfaceDeepest: 'var(--romd-surface-deepest)',
  backdrop: 'var(--romd-backdrop)',
  brandGradient: 'var(--romd-brand-gradient)',
  panelMuted: 'var(--romd-panel-muted)',
  panel: 'var(--romd-panel)',
  panelStrong: 'var(--romd-panel-strong)',
  mediaChip: 'var(--romd-media-chip)',
  borderSubtle: 'var(--romd-border-subtle)',
  border: 'var(--romd-border)',
  borderStrong: 'var(--romd-border-strong)',
  textStrong: 'var(--romd-text-strong)',
  textBody: 'var(--romd-text-body)',
  textMuted: 'var(--romd-text-muted)',
  textFaint: 'var(--romd-text-faint)',
  textDisabled: 'var(--romd-text-disabled)',
  focusRing: 'var(--romd-focus-ring)',
  fontDisplay: 'var(--romd-font-display)',
  fontMono: 'var(--romd-font-mono)',
  contentMax: 'var(--romd-content-max)',
  pageGap: 'var(--romd-page-gap)',
  sectionGap: 'var(--romd-section-gap)',
} as const;
