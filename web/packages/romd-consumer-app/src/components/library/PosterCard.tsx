import { Box, Text } from '@mantine/core';
import type {
  ConsumerCollectionTitle,
  ConsumerTitleCard,
  RecentlyPlayedTitle,
} from '@romd/consumer-api-client';
import { GamePosterSurface } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';
import { IconPlayerPlayFilled } from '@tabler/icons-react';
import { useBrowserPlaybackAvailability } from '../../hooks/useBrowserPlaybackCapability';
import { TitleLink } from '../navigation/TitleLink';
import { ArtworkFallback } from './ArtworkFallback';
import { ArtworkImage } from './ArtworkImage';

type PosterTitle = ConsumerTitleCard | ConsumerCollectionTitle | RecentlyPlayedTitle;

interface PosterCardProps {
  title: PosterTitle;
  showPlayAction?: boolean;
}

export function PosterCard({ title, showPlayAction = true }: PosterCardProps) {
  const defaultReleaseId = title.defaultReleaseId ?? null;
  const playback = useBrowserPlaybackAvailability(title.system.key);

  return (
    <Box className="romd-poster">
      <Box
        component={TitleLink}
        to={`/titles/${title.id}`}
        aria-label={title.name}
        style={{
          display: 'block',
          color: 'inherit',
          textDecoration: 'none',
        }}
      >
        <GamePosterSurface>
          <ArtworkImage
            artwork={title.artwork}
            style={{
              aspectRatio: 'var(--romd-poster-aspect)',
            }}
            fallback={<ArtworkFallback name={title.name} />}
            missingArtworkLabel={
              <Text
                className="romd-card-title romd-poster-fallback-name"
                lineClamp={4}
                title={title.name}
              >
                {title.name}
              </Text>
            }
          />
        </GamePosterSurface>
      </Box>

      {showPlayAction && defaultReleaseId && playback.available && (
        <Box
          className="romd-poster-play"
          component={TitleLink}
          to={`/titles/${title.id}/releases/${defaultReleaseId}/play`}
          aria-label={`Play ${title.name}`}
          style={{
            position: 'absolute',
            inset: 0,
            margin: 'auto',
            width: 52,
            height: 52,
            borderRadius: '50%',
            display: 'grid',
            placeItems: 'center',
            color: 'var(--romd-on-accent)',
            background: 'var(--romd-accent)',
            boxShadow: '0 10px 30px -8px var(--romd-accent-glow)',
          }}
        >
          <IconPlayerPlayFilled size={22} />
        </Box>
      )}
    </Box>
  );
}
