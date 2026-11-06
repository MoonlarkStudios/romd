import {
  type CSSVariablesResolver,
  createTheme,
  defaultVariantColorsResolver,
  SegmentedControl,
  v8CssVariablesResolver,
} from '@mantine/core';
import { romdColors, romdFonts } from './tokens';

function actionCssVariables(accent: string, hover: string, onAccent: string) {
  return {
    '--mantine-primary-color-light-color': accent,
    '--mantine-primary-color-contrast': onAccent,
    ...Object.fromEntries(
      [
        'teal',
        'mint',
        'pine',
      ].flatMap((color) =>
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

/** Match the HTML authentication canvas without losing Mantine's variant colors. */
export const portalCssVariablesResolver: CSSVariablesResolver = (theme) => {
  const resolved = v8CssVariablesResolver(theme);
  const portal = {
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
  const actions = actionCssVariables(
    'var(--romd-portal-accent)',
    'var(--romd-portal-accent-hover)',
    'var(--romd-portal-on-accent)',
  );
  return {
    ...resolved,
    light: {
      ...resolved.light,
      ...portal,
      ...actions,
    },
    dark: {
      ...resolved.dark,
      ...portal,
      ...actions,
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
