import { Box, type BoxProps } from '@mantine/core';
import { romdColors } from '@romd/foundation';
import type { ReactNode } from 'react';
import classes from './ArtworkSurface.module.css';

export type ArtworkBackground = 'dark' | 'light' | 'checkerboard';

/** Neutral backgrounds make transparent white and black artwork inspectable. */
export function ArtworkSurface({ background = 'checkerboard', children, ...props }: BoxProps & {
  background?: ArtworkBackground;
  children: ReactNode;
}) {
  return <Box {...props} className={classes.surface} data-background={background} style={[props.style, { '--artwork-dark': romdColors.surfaceRaised, '--artwork-light': romdColors.textStrong, '--artwork-checker-light': romdColors.textMuted, '--artwork-checker-dark': romdColors.surfaceHighest }]}>{children}</Box>;
}
