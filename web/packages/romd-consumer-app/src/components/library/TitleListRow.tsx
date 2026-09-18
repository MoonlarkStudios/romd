import { Badge, Box, Button, Group, Paper, Stack, Text } from '@mantine/core';
import type {
  ConsumerCollectionTitleDto,
  ConsumerTitleCardDto,
  RecentlyPlayedTitleDto,
} from '@romd/consumer-api-client';
import { IconDownload, IconPlayerPlay, IconStar } from '@tabler/icons-react';
import { useBrowserPlaybackAvailability } from '../../hooks/useBrowserPlaybackCapability';
import { formatCount, formatRating } from '../../utils/format';
import { TitleLink } from '../navigation/TitleLink';
import { ArtworkFallback } from './ArtworkFallback';
import { ArtworkImage } from './ArtworkImage';

type TitleRowData = ConsumerTitleCardDto | ConsumerCollectionTitleDto | RecentlyPlayedTitleDto;

interface TitleListRowProps {
  title: TitleRowData;
}

export function TitleListRow({ title }: TitleListRowProps) {
  const defaultReleaseId = title.defaultReleaseId ?? null;
  const playback = useBrowserPlaybackAvailability(title.system.key);
  const rating = formatRating(title.rating);
  const releaseCount = Number(title.releaseCount);

  return (
    <Paper
      p="sm"
      withBorder
      style={{ background: 'var(--romd-panel-strong)' }}
    >
      <Group
        gap="md"
        wrap="nowrap"
        align="center"
      >
        <Box
          component={TitleLink}
          to={`/titles/${title.id}`}
          style={{
            color: 'inherit',
            textDecoration: 'none',
            flex: '0 0 64px',
          }}
        >
          <ArtworkImage
            artwork={title.artwork}
            style={{ width: 64 }}
            fallback={<ArtworkFallback name={title.name} fontSize={24} />}
          />
        </Box>

        <Stack
          gap={4}
          style={{ minWidth: 0, flex: 1 }}
        >
          <Text
            component={TitleLink}
            to={`/titles/${title.id}`}
            fw={800}
            truncate
            style={{
              color: 'inherit',
              textDecoration: 'none',
            }}
          >
            {title.name}
          </Text>
          <Group
            gap="xs"
            wrap="wrap"
          >
            <Badge color="gray">{title.system.name}</Badge>
            {title.genre && <Badge color="gray">{title.genre}</Badge>}
            {rating && (
              <Badge
                color="bronze"
                leftSection={<IconStar size={12} />}
              >
                {rating}
              </Badge>
            )}
            <Badge color="gray">
              {formatCount(title.releaseCount)} {releaseCount === 1 ? 'Release' : 'Releases'}
            </Badge>
          </Group>
        </Stack>

        <TitleAction
          titleId={title.id}
          releaseId={playback.available ? defaultReleaseId : null}
        />
      </Group>
    </Paper>
  );
}

interface TitleActionProps {
  titleId: string;
  releaseId: string | null;
}

function TitleAction({ titleId, releaseId }: TitleActionProps) {
  if (releaseId) {
    return (
      <Button
        component={TitleLink}
        to={`/titles/${titleId}/releases/${releaseId}/play`}
        color="mint"
        leftSection={<IconPlayerPlay size={16} />}
      >
        Play
      </Button>
    );
  }

  return (
    <Button
      component={TitleLink}
      to={`/titles/${titleId}`}
      variant="light"
      color="gray"
      leftSection={<IconDownload size={16} />}
    >
      Details
    </Button>
  );
}
