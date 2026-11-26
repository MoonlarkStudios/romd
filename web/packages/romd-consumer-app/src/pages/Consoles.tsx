import { Button, Group, NativeSelect, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useLocalStorage } from '@mantine/hooks';
import type { ConsumerPlatformSummary } from '@romd/consumer-api-client';
import { IconArrowRight } from '@tabler/icons-react';
import { Link } from 'react-router';
import { LibraryUnavailable } from '../components/status/LibraryUnavailable';
import { StatusState } from '../components/status/StatusState';
import { usePlatforms } from '../hooks/useConsumerLibrary';
import { ConsumerLibraryUnavailableError } from '../services/libraryErrors';
import { ConsoleLogo, useAvailableArtwork } from './ConsoleArtwork';
import classes from './Consoles.module.css';

function ConsoleCard({ system }: { system: ConsumerPlatformSummary }) {
  const portrait = useAvailableArtwork(system.consoleArtwork);
  return (
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
            order={2}
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
  );
}

export function Consoles() {
  const [savedSort, setSavedSort] = useLocalStorage({
    key: 'romd.consumer.consoles.sort',
    defaultValue: 'releaseYear',
    getInitialValueInEffect: false,
  });
  const sortBy = savedSort === 'name' || savedSort === 'titleCount' ? savedSort : 'releaseYear';
  const systems = usePlatforms(sortBy);
  const items = systems.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Stack gap="var(--romd-page-gap)">
      <Group
        justify="space-between"
        align="center"
      >
        <Title
          order={1}
          className={classes.pageHeading}
        >
          Consoles
        </Title>
        <NativeSelect
          label="Sort by"
          value={sortBy}
          onChange={(event) => setSavedSort(event.currentTarget.value)}
          data={[
            {
              value: 'releaseYear',
              label: 'Release order',
            },
            {
              value: 'titleCount',
              label: 'Most games',
            },
            {
              value: 'name',
              label: 'Name (A–Z)',
            },
          ]}
        />
      </Group>
      {systems.error instanceof ConsumerLibraryUnavailableError ? (
        <LibraryUnavailable retry={() => void systems.refetch()} />
      ) : (
        <>
          {items.length > 0 && (
            <SimpleGrid
              cols={{
                base: 1,
                xs: 2,
                lg: 3,
              }}
              spacing="lg"
            >
              {items.map((system) => (
                <div
                  key={system.key}
                  className={classes.gridItem}
                >
                  {sortBy === 'releaseYear' && (
                    <div className={`romd-metadata ${classes.year}`}>
                      {system.releaseYear == null ? (
                        'Year unknown'
                      ) : (
                        <time dateTime={String(system.releaseYear)}>{system.releaseYear}</time>
                      )}
                    </div>
                  )}
                  <ConsoleCard system={system} />
                </div>
              ))}
            </SimpleGrid>
          )}
          {systems.isLoading && (
            <StatusState
              kind="loading"
              label="Loading consoles"
            />
          )}
          {systems.isError && !systems.isFetchNextPageError && (
            <StatusState
              kind="error"
              message="Your consoles could not be loaded."
              onRetry={() => void systems.refetch()}
              retrying={systems.isRefetching}
            />
          )}
          {systems.isSuccess && items.length === 0 && (
            <StatusState
              kind="empty"
              title="No consoles yet"
              message="Consoles appear here when your library has games for them."
            />
          )}
          {systems.hasNextPage && (
            <Group justify="center">
              <Button
                variant="light"
                onClick={() => void systems.fetchNextPage()}
                loading={systems.isFetchingNextPage}
              >
                {systems.isFetchNextPageError ? 'Retry loading consoles' : 'Load more consoles'}
              </Button>
            </Group>
          )}
        </>
      )}
    </Stack>
  );
}
