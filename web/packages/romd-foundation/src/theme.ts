import {
  type CSSVariablesResolver,
  createTheme,
  defaultVariantColorsResolver,
  SegmentedControl,
  v8CssVariablesResolver,
} from '@mantine/core';
import { romdColors, romdFonts } from './tokens';

function actionCssVariables(
  accent: string,
  hover: string,
  onAccent: string,
  colors = [
    'teal',
    'mint',
    'pine',
  ],
) {
  return {
    '--mantine-primary-color-light-color': accent,
    '--mantine-primary-color-contrast': onAccent,
    ...Object.fromEntries(
      colors.flatMap((color) =>
        Object.entries({
          filled: accent,
          'filled-hover': hover,
          'light-color': accent,
          outline: accent,
          contrast: onAccent,
        }).map(
          ([role, value]) =>
            [
              `--mantine-color-${color}-${role}`,
              value,
            ] as const,
        ),
      ),
    ),
  };
}

/** Embedded consumer UI owns dark actions even inside a light host. */
export const darkCssVariablesResolver: CSSVariablesResolver = (theme) => {
  const resolved = v8CssVariablesResolver(theme);
  const actions = actionCssVariables(
    romdColors.accent,
    romdColors.accentHover,
    romdColors.onAccent,
  );
  return {
    ...resolved,
    dark: {
      ...resolved.dark,
      ...actions,
    },
    light: {
      ...resolved.light,
      ...actions,
    },
  };
};

const portalColors = {
  '--mantine-color-body': 'var(--romd-portal-bg)',
  '--mantine-color-text': 'var(--romd-portal-text)',
  '--mantine-color-dimmed': 'var(--romd-portal-muted)',
  '--mantine-color-default': 'var(--romd-portal-surface)',
  '--mantine-color-default-hover': 'var(--romd-portal-overlay)',
  '--mantine-color-default-border': 'var(--romd-portal-border)',
  '--mantine-color-placeholder': 'var(--romd-portal-muted)',
  '--mantine-color-anchor': 'var(--romd-portal-accent)',
  '--mantine-color-error': 'var(--romd-portal-error)',
};

function resolvePortalColors(
  theme: Parameters<CSSVariablesResolver>[0],
  colors: Record<string, string>,
) {
  const resolved = v8CssVariablesResolver(theme);
  return {
    ...resolved,
    light: {
      ...resolved.light,
      ...portalColors,
      ...colors,
    },
    dark: {
      ...resolved.dark,
      ...portalColors,
      ...colors,
    },
  };
}

const portalAccent = [
  'var(--romd-portal-accent)',
  'var(--romd-portal-accent-hover)',
  'var(--romd-portal-on-accent)',
] as const;

/** Match the HTML authentication canvas without losing Mantine's variant colors. */
export const portalCssVariablesResolver: CSSVariablesResolver = (theme) =>
  resolvePortalColors(theme, actionCssVariables(...portalAccent));

function statusCssVariables(colors: string[], foreground: string, surface: string) {
  return Object.fromEntries(
    colors.flatMap((color) =>
      Object.entries({
        text: foreground,
        filled: foreground,
        'filled-hover': `color-mix(in srgb, ${foreground}, var(--romd-portal-bg) 8%)`,
        light: surface,
        'light-color': foreground,
        'light-hover': `color-mix(in srgb, ${surface}, ${foreground} 8%)`,
        outline: foreground,
        'outline-hover': surface,
      }).map(([role, value]) => [
        `--mantine-color-${color}-${role}`,
        value,
      ]),
    ),
  );
}

/** Adapt the Admin first-paint palette to Mantine's surfaces and variants. */
export const adminCssVariablesResolver: CSSVariablesResolver = (theme) => {
  const primary = `--mantine-color-${theme.primaryColor}`;
  const resolved = resolvePortalColors(theme, {
    '--mantine-color-default-color': 'var(--romd-portal-text)',
    // Explicit legacy accents still use the foundation ramps in Admin.
    '--mantine-color-teal-contrast':
      'light-dark(var(--mantine-color-white), var(--mantine-color-dark-9))',
    ...actionCssVariables(...portalAccent, [
      theme.primaryColor,
    ]),
    [`${primary}-text`]: 'var(--romd-portal-accent)',
    [`${primary}-light`]: 'var(--romd-admin-selected)',
    [`${primary}-light-hover`]:
      'color-mix(in srgb, var(--romd-admin-selected), var(--romd-portal-accent) 10%)',
    [`${primary}-light-color`]: 'var(--romd-admin-selected-text)',
    '--mantine-primary-color-light-color': 'var(--romd-admin-selected-text)',
    '--mantine-color-gray-light': 'var(--romd-portal-overlay)',
    '--mantine-color-gray-light-color': 'var(--romd-portal-muted)',
    '--mantine-color-gray-light-hover': 'var(--romd-portal-overlay)',
    '--mantine-color-success': 'var(--romd-admin-success)',
    ...statusCssVariables(
      [
        'green',
      ],
      'var(--romd-admin-success)',
      'var(--romd-admin-success-surface)',
    ),
    ...statusCssVariables(
      [
        'yellow',
        'orange',
      ],
      'var(--romd-admin-warning)',
      'var(--romd-admin-warning-surface)',
    ),
    ...statusCssVariables(
      [
        'red',
      ],
      'var(--romd-portal-error)',
      'var(--romd-admin-danger-surface)',
    ),
  });
  return {
    ...resolved,
    dark: {
      ...resolved.dark,
      '--mantine-color-dark-0': 'var(--romd-portal-text)',
      '--mantine-color-dark-2': 'var(--romd-portal-muted)',
      '--mantine-color-dark-4': 'var(--romd-portal-border)',
      '--mantine-color-dark-5': 'var(--romd-portal-overlay)',
      '--mantine-color-dark-6': 'var(--romd-portal-surface)',
      '--mantine-color-dark-7': 'var(--romd-portal-bg)',
    },
  };
};

/** Moonlark adapter for Mantine. Legacy mint/pine names share the teal action ramp. */
const actionColors = [
  '#edf8f4',
  '#d9eee6',
  '#bce1d5',
  '#9ed5c7',
  '#88c9b9',
  '#69b7a5',
  '#3e8877',
  '#246758',
  '#1d5549',
  '#17443b',
] as const;

export const romdTheme = createTheme({
  primaryColor: 'teal',
  primaryShade: {
    light: 7,
    dark: 4,
  },
  autoContrast: true,
  variantColorResolver: (input) => {
    const resolved = defaultVariantColorsResolver(input);
    const color = input.color || input.theme.primaryColor;
    // Mantine resolves non-virtual contrast without the active scheme.
    return input.variant === 'filled' &&
      [
        'teal',
        'mint',
        'pine',
      ].includes(color)
      ? {
          ...resolved,
          color: 'var(--mantine-color-teal-contrast)',
        }
      : resolved;
  },
  fontFamily: romdFonts.display,
  fontFamilyMonospace: romdFonts.mono,
  headings: {
    fontFamily: romdFonts.display,
  },
  defaultRadius: 'md',
  colors: {
    pine: actionColors,
    mint: actionColors,
    teal: actionColors,
    bronze: [
      '#FDF6E3',
      '#F8ECC9',
      '#F2DDA0',
      '#ECCF86',
      '#E6C074',
      '#D9AB58',
      '#C79540',
      '#A67A2E',
      '#866021',
      '#654717',
    ],
    dark: [
      '#f4efe4',
      '#c9d1cd',
      '#adb7b6',
      '#788782',
      '#4b5653',
      '#2b3032',
      '#222628',
      '#191b1c',
      '#141718',
      '#101213',
    ],
  },
  components: {
    SegmentedControl: SegmentedControl.extend({
      classNames: (_theme, { color }) =>
        color &&
        [
          'teal',
          'mint',
          'pine',
        ].includes(color)
          ? {
              label: 'romd-accent-segment-label',
            }
          : {},
      styles: (_theme, { color }) =>
        color &&
        [
          'teal',
          'mint',
          'pine',
        ].includes(color)
          ? {
              root: {
                '--sc-color': 'var(--mantine-color-teal-filled)',
              },
            }
          : {},
    }),
    Button: {
      defaultProps: {
        size: 'sm',
      },
    },
    TextInput: {
      defaultProps: {
        size: 'sm',
      },
    },
    Select: {
      defaultProps: {
        size: 'sm',
      },
    },
    Badge: {
      defaultProps: {
        variant: 'light',
      },
    },
    Card: {
      defaultProps: {
        radius: 'lg',
      },
      styles: {
        root: {
          boxShadow: '0 0 0 1px var(--romd-border-subtle)',
        },
      },
    },
    Paper: {
      styles: {
        root: {
          boxShadow: '0 0 0 1px var(--romd-border-subtle)',
        },
      },
    },
  },
});
