import { createTheme } from '@mantine/core';
import { romdFonts } from './tokens';

/**
 * ROMD Mantine theme — maps the foundation tokens into Mantine so the
 * identity flows through components (buttons, badges, focus, alerts)
 * instead of being reinvented per call site.
 *
 * Anchored on the console client's dark skin ("the archive at night").
 * `mint[4]` and `bronze[4]` hold the canonical accent values; the `dark`
 * tuple is the console's text/surface ladder in Mantine order (light text
 * to deep background), so Mantine-derived colors (body, dimmed, borders)
 * inherit the identity automatically.
 */
export const romdTheme = createTheme({
  primaryColor: 'mint',
  primaryShade: { light: 7, dark: 4 },
  autoContrast: true,
  fontFamily: romdFonts.display,
  fontFamilyMonospace: romdFonts.mono,
  headings: {
    fontFamily: romdFonts.display,
  },
  defaultRadius: 'md',
  colors: {
    mint: [
      '#EAFFF7',
      '#CCFCEC',
      '#9FF5D8',
      '#6FECC4',
      '#4FE3B0',
      '#36D39C',
      '#22B982',
      '#199A6D',
      '#127C58',
      '#0C5F43',
    ],
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
      '#EEF4F5',
      '#B3C0C3',
      '#9FB0B4',
      '#6F8188',
      '#566970',
      '#121D22',
      '#0D1519',
      '#070D10',
      '#05090B',
      '#030607',
    ],
  },
  components: {
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
