import { Alert, Badge, Button, Group, Loader, Select, Stack, Text, Title } from '@mantine/core';
import {
  type CollectionDetail,
  type CollectionItemDto,
  evaluateLibrary,
  type LibraryDto,
  type LibraryEvaluationTitleDto,
} from '@romd/admin-api-client';
import { IconDeviceGamepad2 } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useLibraryPreview } from '../../hooks/api/useLibraryExperience';
import { libraryManagementKeys, useLibrary } from '../../hooks/api/useLibraryManagement';
import { exclusionReasons } from '../Libraries/libraryEvaluation';
import { useCollectionPlacements } from './CollectionAudiences';
import classes from './Collections.module.css';

export function CollectionPreview({ collection }: { collection: CollectionDetail }) {
  const placements = useCollectionPlacements(collection.id);
  const [selected, setSelected] = useState<string | null>(null);
  const audience = placements.data?.find((p) => p.libraryId === selected) ?? placements.data?.[0];
  const library = useLibrary(audience?.libraryId ?? null);
  return (
    <Stack gap="lg">
      <Group
        justify="space-between"
        align="flex-start"
      >
        <div>
          <Title
            order={2}
            className={classes.sectionHeading}
          >
            Through their eyes
          </Title>
          <Text
            c="dimmed"
            mt="xs"
            size="sm"
          >
            Saved, owned games that this audience can discover, in collection order.
          </Text>
        </div>
        <Select
          label="Preview as audience"
          placeholder="Choose an attached library"
          value={audience?.libraryId ?? null}
          onChange={setSelected}
          allowDeselect={false}
          data={(placements.data ?? []).map((p) => ({
            value: p.libraryId,
            label: p.name,
          }))}
        />
      </Group>
      {placements.isError ? (
        <Alert color="red">
          Could not load attached audiences.{' '}
          <Button
            variant="subtle"
            onClick={() => placements.refetch()}
          >
            Retry
          </Button>
        </Alert>
      ) : placements.isPending ? (
        <Loader size="sm" />
      ) : !audience ? (
        <div className={classes.panel}>
          <Title
            order={3}
            className={classes.sectionHeading}
          >
            Choose who gets this experience
          </Title>
          <Text
            c="dimmed"
            mt="sm"
          >
            Attach the collection to a library in Audiences, then return here to preview it.
          </Text>
        </div>
      ) : library.isError ? (
        <Alert color="red">
          Could not load the audience’s policy.{' '}
          <Button
            variant="subtle"
            onClick={() => library.refetch()}
          >
            Retry
          </Button>
        </Alert>
      ) : !library.data ? (
        <Loader size="sm" />
      ) : library.data.needsMaterialization ? (
        <Alert color="yellow">
          This audience’s games are updating. Preview will be available after its saved rules finish
          applying.
        </Alert>
      ) : library.data.configurationState !== 'Valid' ? (
        <Alert color="yellow">
          This library’s policy needs attention.{' '}
          <Text
            component={Link}
            to={`/libraries/${audience.libraryId}?tab=games`}
          >
            Review game rules
          </Text>
        </Alert>
      ) : (
        <AudienceGames
          key={audience.libraryId}
          collection={collection}
          library={library.data}
        />
      )}
    </Stack>
  );
}

function AudienceGames({
  collection,
  library,
}: {
  collection: CollectionDetail;
  library: LibraryDto;
}) {
  const preview = useLibraryPreview(library.id, collection.id);
  const [inspect, setInspect] = useState<string | null>(null);
  const items = preview.data?.pages.flatMap((p) => p.items) ?? [];
  const chosen = collection.items.find((t) => t.titleId === inspect);
  return (
    <>
      <div className={classes.panel}>
        <Group
          justify="space-between"
          mb="lg"
        >
          <Title
            order={3}
            className={classes.sectionHeading}
          >
            {collection.name}
          </Title>
          <Badge color="teal">{library.name}</Badge>
        </Group>
        {preview.isError ? (
          <Alert color="red">
            Could not load preview.{' '}
            <Button
              variant="subtle"
              onClick={() => preview.refetch()}
            >
              Retry
            </Button>
          </Alert>
        ) : preview.isPending ? (
          <Loader size="sm" />
        ) : items.length === 0 ? (
          <Text c="dimmed">
            No owned games in this collection are visible to {library.name}. Investigate a game
            below to see how the library rules apply.
          </Text>
        ) : (
          <div className={classes.previewGrid}>
            {items.map((game) => (
              <div key={game.id}>
                {game.coverUrl ? (
                  <img
                    className={classes.previewArt}
                    src={game.coverUrl}
                    alt=""
                    loading="lazy"
                  />
                ) : (
                  <span className={classes.previewArt}>
                    <IconDeviceGamepad2 size={32} />
                  </span>
                )}
                <Text
                  fw={600}
                  size="sm"
                  mt="sm"
                >
                  {game.name}
                </Text>
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {game.platformName}
                </Text>
              </div>
            ))}
          </div>
        )}
        {preview.hasNextPage && (
          <Button
            variant="light"
            mt="lg"
            loading={preview.isFetchingNextPage}
            onClick={() => preview.fetchNextPage()}
          >
            More visible games
          </Button>
        )}
      </div>
      <div className={classes.panel}>
        <Stack gap="md">
          <Title
            order={3}
            className={classes.sectionHeading}
          >
            Missing a game?
          </Title>
          <Text
            size="sm"
            c="dimmed"
          >
            Collection membership never overrides library policy. Inspect any game in this
            collection against the saved rules.
          </Text>
          <Select
            label="Investigate a game"
            placeholder="Choose a collection game"
            searchable
            clearable
            value={inspect}
            onChange={setInspect}
            data={collection.items.map((t) => ({
              value: t.titleId,
              label: t.titleName,
            }))}
          />
          {chosen && (
            <GameExplanation
              key={`${library.id}-${chosen.titleId}`}
              library={library}
              item={chosen}
            />
          )}
        </Stack>
      </div>
    </>
  );
}

function GameExplanation({ library, item }: { library: LibraryDto; item: CollectionItemDto }) {
  const query = useQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'collection-explanation',
      library.id,
      library.configuration,
      item.titleId,
      item.titleName,
    ],
    retry: false,
    queryFn: async ({ signal }): Promise<LibraryEvaluationTitleDto | null> => {
      if (!library.configuration) throw new Error('The library policy is unavailable.');
      let cursor: string | undefined;
      const visited = new Set<string>();
      do {
        const response = await evaluateLibrary({
          path: {
            libraryId: library.id,
          },
          body: {
            configuration: library.configuration,
            view: 'all',
            search: item.titleName.slice(0, 200),
            cursor,
          },
          signal,
        });
        if (response.error || !response.data)
          throw new Error('Could not evaluate this game. The catalog may still be updating.');
        const found = response.data.items.find((t) => t.id === item.titleId);
        if (found) return found;
        cursor = response.data.nextCursor ?? undefined;
        if (cursor && visited.has(cursor))
          throw new Error('The catalog changed during inspection. Try again.');
        if (cursor) visited.add(cursor);
      } while (cursor);
      return null;
    },
  });
  return (
    <Stack gap="sm">
      {query.isError ? (
        <Alert color="red">
          {query.error.message}{' '}
          <Button
            variant="subtle"
            onClick={() => query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      ) : query.isPending ? (
        <Group>
          <Loader size="sm" />
          <Text size="sm">Checking saved rules…</Text>
        </Group>
      ) : !query.data ? (
        <Text c="dimmed">
          This title was not found in the current catalog evaluation. Open its metadata to
          investigate.
        </Text>
      ) : (
        <>
          <Text fw={600}>
            {query.data.isEligible && query.data.isOwned
              ? `Visible to ${library.name}`
              : `Hidden from ${library.name}`}
          </Text>
          <Text size="sm">
            {query.data.reason
              ? (exclusionReasons[query.data.reason] ?? query.data.reason)
              : !query.data.isOwned
                ? 'The game is eligible, but there is no owned release to show in a collection.'
                : query.data.isPlayable
                  ? 'An owned release has all required files. Device compatibility is checked separately.'
                  : 'The game is visible, but its owned release files are incomplete.'}
          </Text>
          {query.data.ratingCategory && (
            <Text
              size="sm"
              c="dimmed"
            >
              Selected rating: {query.data.ratingBoard?.toUpperCase()} · {query.data.ratingCategory}
              {query.data.ratingAge != null ? ` · age ${query.data.ratingAge}` : ''}
            </Text>
          )}
        </>
      )}
      <Group>
        <Button
          component={Link}
          to={`/libraries/${library.id}?tab=games`}
          variant="light"
          {...workspaceActionProps}
        >
          Review library policy
        </Button>
        <Button
          component={Link}
          to={`/titles/${item.titleId}`}
          variant="subtle"
        >
          Inspect title and releases
        </Button>
      </Group>
    </Stack>
  );
}
