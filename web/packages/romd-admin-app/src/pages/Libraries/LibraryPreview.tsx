import {
  Alert,
  Button,
  Group,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { GameCover } from '../../components/GameCover';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useLibraryAttachments, useLibraryPreview } from '../../hooks/api/useLibraryExperience';
import classes from './Libraries.module.css';

export function LibraryPreview({ libraryId }: { libraryId: string }) {
  const [collection, setCollection] = useState<string | null>(null);
  const [params] = useSearchParams();
  const gameSelection = new URLSearchParams(params);
  gameSelection.set('tab', 'games');
  const attachments = useLibraryAttachments(libraryId);
  const preview = useLibraryPreview(libraryId, collection ?? undefined);
  const items = preview.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <Stack gap="lg">
      <Group
        justify="space-between"
        align="flex-end"
      >
        <Title
          order={2}
          className={classes.sectionHeading}
        >
          Library preview
        </Title>
        {!!attachments.data?.length && (
          <Select
            label="Filter by collection"
            placeholder="All owned games"
            clearable
            clearButtonProps={{
              'aria-label': 'Clear collection filter',
              'aria-hidden': false,
              tabIndex: 0,
            }}
            w={260}
            maw="100%"
            value={collection}
            onChange={setCollection}
            data={attachments.data.map((a) => ({
              value: a.collectionId,
              label: a.name,
            }))}
          />
        )}
      </Group>
      <div className={classes.preview}>
        {preview.isError ? (
          <Alert color="red">
            Could not load the preview.{' '}
            <Button
              variant="subtle"
              onClick={() => preview.refetch()}
            >
              Retry
            </Button>
          </Alert>
        ) : preview.isPending ? (
          <Skeleton h={260} />
        ) : items.length === 0 ? (
          <div className={classes.empty}>
            <Title
              order={3}
              className={classes.sectionHeading}
            >
              {collection ? 'No visible games in this collection' : 'No owned games visible'}
            </Title>
            <Button
              component={Link}
              to={`?${gameSelection}`}
              variant="light"
              mt="md"
              {...workspaceActionProps}
            >
              Review game selection
            </Button>
          </div>
        ) : (
          <SimpleGrid
            cols={{
              base: 2,
              sm: 3,
              md: 4,
              lg: 6,
            }}
            spacing="lg"
          >
            {items.map((game) => (
              <div key={game.id}>
                <GameCover
                  className={classes.gameArt}
                  url={game.coverUrl}
                  iconSize={32}
                />
                <Text
                  fw={600}
                  size="sm"
                  mt="sm"
                  lineClamp={2}
                >
                  {game.name}
                </Text>
                <Text
                  c="dimmed"
                  size="xs"
                  mt={4}
                >
                  {game.platformName}
                </Text>
              </div>
            ))}
          </SimpleGrid>
        )}
        {preview.hasNextPage && (
          <Button
            variant="light"
            {...workspaceActionProps}
            mt="xl"
            loading={preview.isFetchingNextPage}
            onClick={() => preview.fetchNextPage()}
          >
            Show more games
          </Button>
        )}
      </div>
    </Stack>
  );
}
