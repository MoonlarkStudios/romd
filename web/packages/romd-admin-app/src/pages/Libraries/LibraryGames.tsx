import {
  Alert,
  Badge,
  Button,
  Group,
  Loader,
  SegmentedControl,
  Select,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import {
  evaluateLibrary,
  type LibraryConfigurationDto,
  type LibraryDto,
} from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { libraryManagementKeys } from '../../hooks/api/useLibraryManagement';
import classes from './Libraries.module.css';
import { LibraryRules } from './LibraryRules';
import { exclusionReasons } from './libraryEvaluation';
import { defaultLibraryConfiguration } from './libraryOptions';

export function LibraryGames({
  library,
  onDirtyChange,
}: {
  library: LibraryDto;
  onDirtyChange: (dirty: boolean) => void;
}) {
  const [configuration, setConfiguration] = useState<LibraryConfigurationDto>(
    library.configuration ?? defaultLibraryConfiguration,
  );
  const [panel, setPanel] = useState('rules');
  return (
    <div>
      <SegmentedControl
        className={classes.mobilePanels}
        fullWidth
        value={panel}
        onChange={setPanel}
        data={[
          {
            value: 'rules',
            label: 'Rules',
          },
          {
            value: 'results',
            label: 'Results',
          },
        ]}
        mb="lg"
      />
      <div className={classes.gamesWorkspace}>
        <div className={panel === 'results' ? classes.mobileHidden : undefined}>
          <LibraryRules
            library={library}
            section="games"
            onDirtyChange={onDirtyChange}
            onConfigurationChange={setConfiguration}
          />
        </div>
        <div className={panel === 'rules' ? classes.mobileHidden : undefined}>
          <DraftResults
            library={library}
            configuration={configuration}
          />
        </div>
      </div>
    </div>
  );
}

function DraftResults({
  library,
  configuration,
}: {
  library: LibraryDto;
  configuration: LibraryConfigurationDto;
}) {
  const [search, setSearch] = useState('');
  const [view, setView] = useState('matching');
  const [cursor, setCursor] = useState<string | undefined>();
  const serialized = JSON.stringify(configuration);
  const [debounced] = useDebouncedValue(serialized, 600);
  const [debouncedSearch] = useDebouncedValue(search, 400);
  const waiting = serialized !== debounced || search !== debouncedSearch;
  // Associate the cursor with the exact draft and filters that produced it.
  const filterKey = JSON.stringify([
    serialized,
    search,
    view,
  ]);
  const [cursorKey, setCursorKey] = useState('');
  const activeCursor = cursorKey === filterKey ? cursor : undefined;
  const query = useQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'evaluation',
      library.id,
      library.configuration,
      debounced,
      view,
      debouncedSearch,
      activeCursor,
    ],
    enabled: !waiting,
    retry: false,
    queryFn: async ({ signal }) => {
      const response = await evaluateLibrary({
        path: {
          libraryId: library.id,
        },
        body: {
          configuration: JSON.parse(debounced) as LibraryConfigurationDto,
          view,
          search: debouncedSearch,
          cursor: activeCursor,
        },
        signal,
      });
      if (response.error || !response.data)
        throw new Error(
          response.error &&
            typeof response.error === 'object' &&
            'detail' in response.error &&
            typeof response.error.detail === 'string'
            ? response.error.detail
            : 'Could not evaluate these rules.',
        );
      return response.data;
    },
  });
  const data = waiting || query.isFetching || query.isError ? undefined : query.data;
  return (
    <Stack
      gap="lg"
      className={classes.resultsPanel}
    >
      <div>
        <Text className={classes.eyebrow}>Before you save</Text>
        <Title
          order={2}
          className={classes.sectionHeading}
          mt="xs"
        >
          See what changes
        </Title>
        <Text
          size="sm"
          c="dimmed"
          mt="xs"
        >
          Draft and saved rules evaluated against the same catalog snapshot. Nothing changes for
          your audience until you save.
        </Text>
      </div>
      <div
        className={classes.changeSummary}
        aria-live="polite"
        aria-busy={!data && !query.isError}
      >
        {query.isError ? (
          <Alert
            color="red"
            title="Evaluation unavailable"
          >
            {query.error.message}
            <Button
              variant="subtle"
              onClick={() => query.refetch()}
            >
              Try again
            </Button>
          </Alert>
        ) : !data ? (
          <Group>
            <Loader size="sm" />
            <Text size="sm">Evaluating your rules…</Text>
          </Group>
        ) : (
          <Stack gap="sm">
            <Text
              fw={650}
              size="lg"
            >
              {data.matchingCount.toLocaleString()} eligible games
            </Text>
            <Group gap="sm">
              <Button
                size="compact-sm"
                variant="light"
                color="teal"
                onClick={() => setView('added')}
              >
                {data.addedCount} added
              </Button>
              <Button
                size="compact-sm"
                variant="light"
                color={data.removedCount ? 'orange' : 'gray'}
                onClick={() => setView('removed')}
              >
                {data.removedCount} removed
              </Button>
            </Group>
            {data.removalReasons.map((reason) => (
              <Text
                key={reason.reason}
                size="sm"
              >
                {reason.count} · {exclusionReasons[reason.reason] ?? reason.reason}
              </Text>
            ))}
            <Text
              size="xs"
              c="dimmed"
            >
              Compared with {data.savedCount} games eligible under saved rules. Counts include
              missing games only when your policy allows them.
            </Text>
          </Stack>
        )}
      </div>
      <TextInput
        label="Find a game in the catalog"
        placeholder="Why isn't this game here?"
        value={search}
        onChange={(e) => {
          setSearch(e.currentTarget.value);
          setView('all');
        }}
      />
      <Select
        label="Show results"
        value={view}
        allowDeselect={false}
        onChange={(v) => {
          if (v) setView(v);
        }}
        data={[
          {
            value: 'matching',
            label: 'Matching games',
          },
          {
            value: 'added',
            label: 'Added by this draft',
          },
          {
            value: 'removed',
            label: 'Removed by this draft',
          },
          {
            value: 'excluded',
            label: 'Excluded games',
          },
          {
            value: 'all',
            label: 'All catalog games',
          },
        ]}
      />
      {data && (
        <>
          {data.items.length === 0 ? (
            <Text c="dimmed">No games match this view and search.</Text>
          ) : (
            <ul className={classes.evaluationList}>
              {data.items.map((game) => (
                <li
                  key={game.id}
                  className={classes.evaluationRow}
                >
                  {game.coverUrl && (
                    <img
                      src={game.coverUrl}
                      alt=""
                      loading="lazy"
                      className={classes.evaluationCover}
                    />
                  )}
                  <Stack
                    gap={5}
                    style={{
                      flex: 1,
                      minWidth: 0,
                    }}
                  >
                    <Group
                      justify="space-between"
                      gap="xs"
                    >
                      <Text fw={600}>{game.name}</Text>
                      <Badge color={game.isEligible ? 'teal' : 'gray'}>
                        {game.isEligible ? 'Eligible' : 'Excluded'}
                      </Badge>
                    </Group>
                    <Text
                      size="xs"
                      c="dimmed"
                    >
                      {game.platformName}
                    </Text>
                    <Text size="sm">
                      {game.reason
                        ? (exclusionReasons[game.reason] ?? game.reason)
                        : game.isPlayable
                          ? 'An owned release has all required files.'
                          : game.isOwned
                            ? 'Owned release files are incomplete.'
                            : 'Eligible, but no owned release is available.'}
                    </Text>
                    {game.ratingCategory && (
                      <Text
                        size="xs"
                        c="dimmed"
                      >
                        Selected rating: {game.ratingBoard?.toUpperCase()} · {game.ratingCategory}
                        {game.ratingAge != null ? ` · age ${game.ratingAge}` : ''}
                      </Text>
                    )}
                    {game.attachedCollectionCount > 0 && (
                      <Text
                        size="xs"
                        c="dimmed"
                      >
                        In {game.attachedCollectionCount} attached collection
                        {game.attachedCollectionCount === 1 ? '' : 's'}. Collection membership does
                        not override library rules.
                      </Text>
                    )}
                    <Button
                      component={Link}
                      to={`/titles/${game.id}`}
                      size="compact-xs"
                      variant="subtle"
                      w="fit-content"
                    >
                      Inspect title and metadata
                    </Button>
                  </Stack>
                </li>
              ))}
            </ul>
          )}
          <Group justify="space-between">
            <Button
              variant="subtle"
              disabled={!activeCursor}
              onClick={() => setCursor(undefined)}
            >
              First page
            </Button>
            <Button
              variant="light"
              disabled={!data.nextCursor}
              onClick={() => {
                setCursorKey(filterKey);
                setCursor(data.nextCursor ?? undefined);
              }}
            >
              Next games
            </Button>
          </Group>
          <Text
            size="xs"
            c="dimmed"
          >
            Evaluated {new Date(data.evaluatedAt).toLocaleTimeString()}. Each page refreshes the
            comparison. Destination compatibility is not evaluated here.
          </Text>
        </>
      )}
    </Stack>
  );
}
