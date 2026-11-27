import { Stack, Text, Title } from '@mantine/core';
import type { ConsumerPlatformSummary } from '@romd/consumer-api-client';
import { IconArrowRight } from '@tabler/icons-react';
import { Link } from 'react-router';
import { ConsoleLogo, useAvailableArtwork } from './ConsoleArtwork';
import classes from './Consoles.module.css';

export function ConsoleCard({
  system,
  headingLevel = 2,
  showReleaseYear = false,
}: {
  system: ConsumerPlatformSummary;
  headingLevel?: 2 | 3;
  showReleaseYear?: boolean;
}) {
  const portrait = useAvailableArtwork(system.consoleArtwork);
  return (
    <div className={classes.gridItem}>
      {showReleaseYear && (
        <div className={`romd-metadata ${classes.year}`}>
          {system.releaseYear == null ? (
            'Year unknown'
          ) : (
            <time dateTime={String(system.releaseYear)}>{system.releaseYear}</time>
          )}
        </div>
      )}
      <Link
        className={classes.card}
        to={`/systems/${encodeURIComponent(system.key)}`}
      >
        <div className={classes.artwork}>
          {portrait.src ? (
            <img
              {...portrait}
              alt=""
              loading="lazy"
              decoding="async"
              className={classes.portrait}
            />
          ) : (
            <ConsoleLogo
              icon={system.icon}
              fallback
            />
          )}
        </div>
        <div className={classes.cardInfo}>
          <Stack gap={4}>
            {portrait.src && <ConsoleLogo icon={system.icon} />}
            <Title
              order={headingLevel}
              className={classes.cardName}
            >
              {system.name}
            </Title>
            <Text
              size="sm"
              c="dimmed"
            >
              {[
                system.manufacturer,
                `${system.titleCount} ${system.titleCount === 1 ? 'game' : 'games'}`,
              ]
                .filter(Boolean)
                .join(' · ')}
            </Text>
          </Stack>
          <span
            className={classes.arrow}
            aria-hidden
          >
            <IconArrowRight size={17} />
          </span>
        </div>
      </Link>
    </div>
  );
}
