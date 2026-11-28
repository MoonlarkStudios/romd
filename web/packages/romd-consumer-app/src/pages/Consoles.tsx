import { Button, Group, NativeSelect, SimpleGrid, Stack, Title } from '@mantine/core';
import { useLocalStorage } from '@mantine/hooks';
import { LibraryUnavailable } from '../components/status/LibraryUnavailable';
import { StatusState } from '../components/status/StatusState';
import { usePlatforms } from '../hooks/useConsumerLibrary';
import { ConsumerLibraryUnavailableError } from '../services/libraryErrors';
import { ConsoleCard } from './ConsoleCard';

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
          className="romd-page-heading"
        >
          Consoles
        </Title>
        <NativeSelect
          aria-label="Sort consoles"
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
        <LibraryUnavailable />
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
                <ConsoleCard
                  key={system.key}
                  system={system}
                  showReleaseYear={sortBy === 'releaseYear'}
                />
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
