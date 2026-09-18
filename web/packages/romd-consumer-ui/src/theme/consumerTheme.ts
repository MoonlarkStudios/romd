import { Button, createTheme, mergeThemeOverrides } from '@mantine/core';
import { romdTheme } from '@romd/foundation/theme';

export const consumerTheme = mergeThemeOverrides(romdTheme, createTheme({
  components: {
    Button: Button.extend({
      styles: (_theme, props) => ({
        root: (props.variant ?? 'filled') === 'filled' && (!props.color || props.color === 'mint')
          ? { color: 'var(--romd-on-accent)' }
          : {},
      }),
    }),
  },
}));
