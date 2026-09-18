import { Box, Button, Group, Stack, Text } from '@mantine/core';
import type { TitleDetail } from '@romd/admin-api-client';
import { GameDetailHero, GameMetadata, useReferenceCatalog } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';
import { IconArrowLeft, IconDownload, IconPlayerPlayFilled } from '@tabler/icons-react';
import { usePlatform } from '../../hooks/api/usePlatforms';
import classes from './BackdropPreview.module.css';
import { ratingPresentation } from './ratingPresentation';

/** Supplies draft artwork to the exact presentation used by consumer game details. */
export function BackdropPreview({ title, mobile }: { title: TitleDetail; mobile: boolean }) {
  const { data: platform } = usePlatform(title.systemKey);
  const catalog = useReferenceCatalog();
  const system = catalog?.systems.find(item => item.key === title.systemKey) ?? { key: title.systemKey, name: platform?.name ?? '', compactLabel: platform?.name ?? '' };
  const backdrop = title.artwork?.find(item => item.role === 'Backdrop');
  const logo = title.artwork?.find(item => item.role === 'Logo');
  const poster = title.artwork?.find(item => item.role === 'Poster');
  return <Stack gap="xs">
    <Text size="sm">Check for built-in logos, edition branding, and subjects hidden by the title. Mobile keeps the image above the text.</Text>
    <Box className={classes.preview} data-mobile={mobile || undefined} data-testid="artwork-composition-preview">
      <GameDetailHero
        name={title.name}
        backdrop={backdrop?.url ? <img src={backdrop.url} alt="" style={{ objectPosition: `${backdrop.focalX ?? 50}% ${backdrop.focalY ?? 50}%` }} /> : undefined}
        poster={poster?.url ? <img src={poster.url} alt="" style={{ width: '100%' }} /> : undefined}
        logoUrl={logo?.url}
        metadata={<GameMetadata system={system} releaseDate={title.releaseDate} genre={title.genre} players={title.players == null ? null : Number(title.players)} rating={title.rating} contentRatings={title.contentRatings?.map(rating => ratingPresentation(catalog, rating))} />}
        backAction={<Button component="span" variant="subtle" color="gray" leftSection={<IconArrowLeft size={16} />}>Back</Button>}
        actions={<Group gap="sm"><Button component="span" size="md" color="mint" leftSection={<IconPlayerPlayFilled size={18} />}>Play Now</Button><Button component="span" size="md" variant="default" leftSection={<IconDownload size={18} />}>Download</Button></Group>}
      />
    </Box>
    <Text size="xs" c="dimmed">Preview only. Actions are illustrative; play availability depends on the selected release. Saving preserves the banner and all other artwork selections.</Text>
  </Stack>;
}
