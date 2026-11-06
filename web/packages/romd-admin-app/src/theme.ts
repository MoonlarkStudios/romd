import { createTheme } from '@mantine/core';
import { romdTheme } from '@romd/foundation/theme';

export const theme = createTheme({
  primaryColor: romdTheme.primaryColor,
  primaryShade: romdTheme.primaryShade,
  autoContrast: true,
  variantColorResolver: romdTheme.variantColorResolver,
  fontFamily: "system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif",
  defaultRadius: 'sm',
  colors: {
    ...romdTheme.colors,
  },
  // Use CSS variables for theming so components adapt to light/dark mode
  // --mantine-color-body: page background
  // --mantine-color-default: component surface
  // --mantine-color-default-border: border color
  other: {
    // Semantic color tokens for consistent light/dark mode support
    surfaceColor: 'var(--mantine-color-default)',
    surfaceColorAlt: 'var(--mantine-color-body)',
    borderColor: 'var(--mantine-color-default-border)',
  },
  components: {
    ...romdTheme.components,
    CloseButton: {
      defaultProps: {
        'aria-label': 'Close',
      },
    },
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
    Paper: {
      // Let Paper use default theme colors instead of hardcoding dark mode
      styles: {
        root: {
          // Subtle border effect that works in both modes
          boxShadow: '0 0 0 1px var(--mantine-color-default-border)',
        },
      },
    },
    Card: {
      // Let Card use default theme colors instead of hardcoding dark mode
      styles: {
        root: {
          // Subtle border effect that works in both modes
          boxShadow: '0 0 0 1px var(--mantine-color-default-border)',
        },
      },
    },
    Badge: {
      defaultProps: {
        variant: 'light',
      },
    },
    Text: {
      styles: (
        _theme: unknown,
        props: {
          className?: string;
        },
      ) => ({
        root: props.className?.includes('prose')
          ? {
              maxWidth: '65ch',
              lineHeight: 1.6,
            }
          : {},
      }),
    },
  },
});
