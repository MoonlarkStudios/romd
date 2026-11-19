import { AppShell, createTheme, defaultVariantColorsResolver, Input, Modal } from '@mantine/core';
import { romdTheme } from '@romd/foundation/theme';
import classes from './theme.module.css';

const statusColors = new Set([
  'blue',
  'green',
  'yellow',
  'orange',
  'red',
]);

export const theme = createTheme({
  primaryColor: romdTheme.primaryColor,
  primaryShade: romdTheme.primaryShade,
  autoContrast: true,
  variantColorResolver: (input) => {
    const resolved = romdTheme.variantColorResolver?.(input) ?? defaultVariantColorsResolver(input);
    if (input.variant !== 'filled') return resolved;

    const color = input.color || input.theme.primaryColor;
    if (color === input.theme.primaryColor)
      return {
        ...resolved,
        color: 'var(--mantine-primary-color-contrast)',
      };
    if (statusColors.has(color))
      return {
        ...resolved,
        color: 'var(--romd-admin-on-status)',
      };
    return resolved;
  },
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
    AppShell: AppShell.extend({
      classNames: {
        root: classes.shell,
        header: classes.shellHeader,
      },
    }),
    Input: Input.extend({
      classNames: {
        wrapper: classes.fieldWrapper,
        input: classes.fieldInput,
      },
    }),
    Modal: Modal.extend({
      classNames: {
        content: classes.panel,
        header: classes.panel,
      },
    }),
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
      classNames: {
        root: classes.panel,
      },
      styles: {
        root: {
          // Subtle border effect that works in both modes
          boxShadow: '0 0 0 1px var(--mantine-color-default-border)',
        },
      },
    },
    Card: {
      classNames: {
        root: classes.panel,
      },
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
