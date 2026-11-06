import { Box, Group, Stack, Title } from '@mantine/core';
import type { ReactNode } from 'react';

export interface GameSpotlightProps {
  /** Heading text and accessible name; stays the accessible name when a logo replaces it visually. */
  name: string;
  /** Hero artwork. When present it fills the surface behind the copy and the poster is not shown. */
  art?: ReactNode;
  /** Shown beside the copy when there is no hero artwork. */
  poster?: ReactNode;
  /** Replaces the visible text title. */
  logo?: ReactNode;
  metadata?: ReactNode;
  /** Host-owned call to action, such as a router link or an inert preview button. */
  actions?: ReactNode;
}

/**
 * The consumer home spotlight slide. Data, routing, and artwork delivery belong to the host. The outer element is
 * the `romd-spotlight` size container, so the layout follows the width it is given rather than the viewport.
 */
export function GameSpotlight({ name, art, poster, logo, metadata, actions }: GameSpotlightProps) {
  return (
    <div className="romd-spotlight-surface">
      <Box
        className="romd-home-spotlight"
        data-has-art={Boolean(art) || undefined}
      >
        {art && <Box className="romd-home-spotlight-art">{art}</Box>}
        <Box className="romd-home-spotlight-content">
          {!art && poster && <Box className="romd-home-spotlight-poster">{poster}</Box>}
          <Stack
            gap="md"
            className="romd-home-spotlight-info"
            style={{
              minWidth: 0,
            }}
          >
            <Title
              order={1}
              className="romd-hero"
              aria-label={name}
            >
              {logo ? (
                <Box
                  className="romd-home-spotlight-logo"
                  aria-hidden="true"
                >
                  {logo}
                </Box>
              ) : (
                name
              )}
            </Title>
            {metadata}
            {actions && (
              <Group
                gap="sm"
                mt="sm"
              >
                {actions}
              </Group>
            )}
          </Stack>
        </Box>
      </Box>
    </div>
  );
}
