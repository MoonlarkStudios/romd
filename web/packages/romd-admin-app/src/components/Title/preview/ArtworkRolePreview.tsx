import { Box, Button, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import type { TitleDetail } from '@romd/admin-api-client';
import {
  artworkAspectRatios,
  artworkCrop,
  artworkObjectStyle,
  ConsumerPresentation,
  DeviceFrame,
  GameDetailHero,
  GameMetadata,
  GamePosterSurface,
  GameSpotlight,
  type PreviewDevice,
  previewDevices,
  useReferenceCatalog,
} from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';
import { IconArrowLeft, IconDownload, IconPlayerPlayFilled } from '@tabler/icons-react';
import type { CSSProperties, ReactNode } from 'react';
import { usePlatform } from '../../../hooks/api/usePlatforms';
import { ArtworkFrame } from '../../ArtworkFrame';
import type { ArtworkRole } from '../../artworkPresentation';
import { ratingPresentation } from '../ratingPresentation';

export type ArtworkPreviewTitle = Pick<
  TitleDetail,
  | 'name'
  | 'systemKey'
  | 'artwork'
  | 'releaseDate'
  | 'genre'
  | 'players'
  | 'rating'
  | 'contentRatings'
>;

export interface ArtworkRolePreviewProps {
  /** The title with any draft artwork already merged in, as the composer builds it today. */
  title: ArtworkPreviewTitle;
  artworkRole: ArtworkRole;
  device: PreviewDevice;
  /** Poster only: the library card or the game details page. */
  posterContext?: 'library' | 'details';
}

/** Mirrors the consumer rail card: clamp(190px, 18vw, 260px), and 150px at 48em and below. */
const posterCardWidth = (device: PreviewDevice) => {
  const { width } = previewDevices[device];
  return width <= 768 ? 150 : Math.min(260, Math.max(190, width * 0.18));
};

/** Mirrors the consumer ArtworkImage frame once its image has loaded, with the same fit, focal, and fallback placement. */
function PreviewImage({
  title,
  artworkRole,
  style,
  fallback,
}: {
  title: ArtworkPreviewTitle;
  artworkRole: ArtworkRole;
  style?: CSSProperties;
  fallback?: ReactNode;
}) {
  const artwork = title.artwork?.find((item) => item.role === artworkRole);
  return (
    <div
      style={{
        position: 'relative',
        width: '100%',
        aspectRatio: artworkAspectRatios[artworkRole],
        overflow: 'hidden',
        ...style,
      }}
    >
      {artwork?.url ? (
        <img
          src={artwork.url}
          alt=""
          style={{
            position: 'absolute',
            inset: 0,
            width: '100%',
            height: '100%',
            ...artworkObjectStyle(artworkRole, artworkCrop(artwork)),
          }}
        />
      ) : (
        fallback && (
          <div
            style={
              artworkRole === 'Logo'
                ? {
                    position: 'relative',
                    minHeight: 'inherit',
                    display: 'grid',
                    placeItems: 'var(--romd-logo-fallback-alignment, center start)',
                  }
                : {
                    position: 'absolute',
                    inset: 0,
                    display: 'grid',
                    placeItems: 'center',
                  }
            }
          >
            {fallback}
          </div>
        )
      )}
    </div>
  );
}

function usePreviewMetadata(title: ArtworkPreviewTitle) {
  const { data: platform } = usePlatform(title.systemKey);
  const catalog = useReferenceCatalog();
  const system = catalog?.systems.find((item) => item.key === title.systemKey) ?? {
    key: title.systemKey,
    name: platform?.name ?? '',
    compactLabel: platform?.name ?? '',
  };
  return (
    <GameMetadata
      system={system}
      releaseDate={title.releaseDate}
      genre={title.genre}
      players={title.players == null ? null : Number(title.players)}
      rating={title.rating}
      contentRatings={title.contentRatings?.map((rating) => ratingPresentation(catalog, rating))}
    />
  );
}

function SpotlightPreview({
  title,
  metadata,
}: {
  title: ArtworkPreviewTitle;
  metadata: ReactNode;
}) {
  const hasHero = title.artwork?.some((item) => item.role === 'Hero' && item.url);
  const hasLogo = title.artwork?.some((item) => item.role === 'Logo');
  return (
    <ConsumerPresentation>
      <GameSpotlight
        name={title.name}
        art={
          hasHero && (
            <PreviewImage
              title={title}
              artworkRole="Hero"
              style={{
                height: '100%',
              }}
            />
          )
        }
        poster={
          <PreviewImage
            title={title}
            artworkRole="Poster"
          />
        }
        logo={
          hasLogo && (
            <PreviewImage
              title={title}
              artworkRole="Logo"
              style={{
                minHeight: 'inherit',
                aspectRatio: 'auto',
              }}
              fallback={<span>{title.name}</span>}
            />
          )
        }
        metadata={metadata}
        actions={
          <Button
            component="span"
            size="md"
            variant="default"
            className="romd-spotlight-details"
          >
            View game
          </Button>
        }
      />
    </ConsumerPresentation>
  );
}

function DetailsPreview({ title, metadata }: { title: ArtworkPreviewTitle; metadata: ReactNode }) {
  const hasBackdrop = title.artwork?.some((item) => item.role === 'Backdrop' && item.url);
  return (
    <GameDetailHero
      name={title.name}
      backdrop={
        hasBackdrop ? (
          <PreviewImage
            title={title}
            artworkRole="Backdrop"
            style={{
              height: '100%',
              aspectRatio: 'auto',
            }}
          />
        ) : undefined
      }
      poster={
        <PreviewImage
          title={title}
          artworkRole="Poster"
        />
      }
      logoUrl={title.artwork?.find((item) => item.role === 'Logo')?.url}
      metadata={metadata}
      backAction={
        <Button
          component="span"
          variant="subtle"
          color="gray"
          leftSection={<IconArrowLeft size={16} />}
        >
          Back
        </Button>
      }
      actions={
        <Group gap="sm">
          <Button
            component="span"
            size="md"
            color="mint"
            leftSection={<IconPlayerPlayFilled size={18} />}
          >
            Play Now
          </Button>
          <Button
            component="span"
            size="md"
            variant="default"
            leftSection={<IconDownload size={18} />}
          >
            Download
          </Button>
        </Group>
      }
    />
  );
}

/**
 * Renders a role's artwork on the consumer surface that shows it, at the chosen device's real width: Banner (Hero) on
 * the home spotlight, Backdrop and Logo on game details, Poster on the library card or game details.
 */
export function ArtworkRolePreview({
  title,
  artworkRole,
  device,
  posterContext = 'library',
}: ArtworkRolePreviewProps) {
  const metadata = usePreviewMetadata(title);
  if (artworkRole === 'Poster' && posterContext === 'library') {
    return (
      <Stack
        gap="xs"
        data-testid="artwork-composition-preview"
      >
        <Box
          mx="auto"
          style={{
            width: posterCardWidth(device),
            maxWidth: '100%',
          }}
        >
          <ConsumerPresentation>
            <GamePosterSurface>
              <PreviewImage
                title={title}
                artworkRole="Poster"
                style={{
                  aspectRatio: 'var(--romd-poster-aspect, 3 / 4)',
                }}
                fallback={<Text ta="center">{title.name}</Text>}
              />
            </GamePosterSurface>
          </ConsumerPresentation>
          <Text
            size="sm"
            fw={500}
            mt="xs"
            style={{
              overflowWrap: 'anywhere',
            }}
          >
            {title.name}
          </Text>
        </Box>
        <Text
          size="xs"
          c="dimmed"
        >
          Consumer library card at {previewDevices[device].label.toLowerCase()} size.
        </Text>
      </Stack>
    );
  }
  return (
    <Stack
      gap="xs"
      data-testid="artwork-composition-preview"
    >
      <DeviceFrame device={device}>
        {artworkRole === 'Hero' ? (
          <SpotlightPreview
            title={title}
            metadata={metadata}
          />
        ) : (
          <DetailsPreview
            title={title}
            metadata={metadata}
          />
        )}
      </DeviceFrame>
      <Text
        size="xs"
        c="dimmed"
      >
        {artworkRole === 'Hero' ? 'Consumer home spotlight' : 'Consumer game details'} · Actions
        shown for composition only.
      </Text>
      {artworkRole === 'Logo' && (
        <details>
          <summary>Inspect logo on light and dark backgrounds</summary>
          <Stack mt="sm">
            <Text size="sm">Inspect the complete logo on both backgrounds before saving.</Text>
            <SimpleGrid
              type="container"
              cols={{
                base: 1,
                '30em': 2,
              }}
            >
              {(
                [
                  'dark',
                  'light',
                ] as const
              ).map((background) => (
                <Stack
                  key={background}
                  gap="xs"
                >
                  <Text size="sm">
                    {background === 'dark' ? 'Dark background' : 'Light background'}
                  </Text>
                  <ArtworkFrame
                    artworkRole="Logo"
                    src={title.artwork?.find((item) => item.role === 'Logo')?.url}
                    title={title.name}
                    fit="Contain"
                    background={background}
                  />
                </Stack>
              ))}
            </SimpleGrid>
          </Stack>
        </details>
      )}
    </Stack>
  );
}
