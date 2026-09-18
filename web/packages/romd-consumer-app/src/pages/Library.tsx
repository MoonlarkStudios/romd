import {
  ActionIcon,
  Box,
  Button,
  Group,
  Pill,
  SegmentedControl,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import {
  IconLayoutGrid,
  IconList,
  IconRefresh,
  IconSearch,
} from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import { PosterCard } from '../components/library/PosterCard';
import { TitleListRow } from '../components/library/TitleListRow';
import { StatusState } from '../components/status/StatusState';
import {
  type ConsumerCatalogSearchParams,
  useCatalogSearch,
  useCurrentLibrary,
} from '../hooks/useConsumerLibrary';

const completenessOptions = [
  { value: 'all', label: 'All' },
  { value: 'complete', label: 'Complete' },
  { value: 'partial', label: 'Partial' },
];

const sortOptions = [
  { value: 'name', label: 'Title (A-Z)' },
  { value: 'rating', label: 'Rating' },
];

export function Library() {
  const [urlSearchParams, setSearchParams] = useSearchParams();
  const [query, setQuery] = useState(urlSearchParams.get('q') ?? '');
  const [viewMode, setViewMode] = useState<'grid' | 'list'>(() =>
    window.localStorage.getItem('romd.library.viewMode') === 'list' ? 'list' : 'grid',
  );
  const [debouncedQuery] = useDebouncedValue(query, 250);
  const systemKey = urlSearchParams.get('systemKey');
  const genre = urlSearchParams.get('genre');
  const completenessParam = urlSearchParams.get('completeness');
  const completeness =
    completenessParam === 'complete' || completenessParam === 'partial' ? completenessParam : 'all';
  const sortBy = urlSearchParams.get('sortBy') === 'rating' ? 'rating' : 'name';

  const libraryQuery = useCurrentLibrary();
  const library = libraryQuery.data;
  const platforms = library?.platforms ?? [];
  const genres = library?.genres ?? [];

  const catalogSearchParams: ConsumerCatalogSearchParams = {
    query: debouncedQuery.trim() || undefined,
    systemKey: systemKey ?? undefined,
    genre: genre ?? undefined,
    completeness: completeness === 'all' ? undefined : (completeness as 'complete' | 'partial'),
    sortBy,
  };

  const catalogQuery = useCatalogSearch(catalogSearchParams, {
    limit: 36,
  });

  const titles = catalogQuery.data?.pages.flatMap((page) => page.items) ?? [];
  const selectedPlatformName = platforms.find((platform) => platform.id === systemKey)?.name;
  const loadedResultCount = titles.length;
  const filteredContext = selectedPlatformName ?? genre ?? (sortBy === 'rating' ? 'Highest rated games' : 'All systems');

  useEffect(() => {
    setQuery(urlSearchParams.get('q') ?? '');
  }, [urlSearchParams]);

  useEffect(() => {
    const nextQuery = debouncedQuery.trim();
    const currentQuery = urlSearchParams.get('q') ?? '';

    if (nextQuery === currentQuery) {
      return;
    }

    const next = new URLSearchParams(urlSearchParams);
    if (nextQuery) {
      next.set('q', nextQuery);
    } else {
      next.delete('q');
    }

    setSearchParams(next, {
      replace: true,
    });
  }, [debouncedQuery, setSearchParams, urlSearchParams]);

  const updateFilter = (name: string, value: string | null) => {
    const next = new URLSearchParams(urlSearchParams);
    if (value && value !== 'all') {
      next.set(name, value);
    } else {
      next.delete(name);
    }

    setSearchParams(next, {
      replace: true,
    });
  };

  const clearFilters = () => {
    setQuery('');
    setSearchParams(
      {},
      {
        replace: true,
      },
    );
  };

  const changeViewMode = (mode: 'grid' | 'list') => {
    setViewMode(mode);
    window.localStorage.setItem('romd.library.viewMode', mode);
  };

  const activeFilters = [
    systemKey && selectedPlatformName
      ? { key: 'systemKey', label: selectedPlatformName, clear: () => updateFilter('systemKey', null) }
      : null,
    genre ? { key: 'genre', label: genre, clear: () => updateFilter('genre', null) } : null,
    completeness !== 'all'
      ? {
          key: 'completeness',
          label: completeness === 'complete' ? 'Complete' : 'Partial',
          clear: () => updateFilter('completeness', null),
        }
      : null,
    query.trim() ? { key: 'q', label: `"${query.trim()}"`, clear: () => setQuery('') } : null,
    sortBy !== 'name'
      ? {
          key: 'sortBy',
          label: 'Top rated first',
          clear: () => updateFilter('sortBy', null),
        }
      : null,
  ].filter((entry): entry is { key: string; label: string; clear: () => void } => entry !== null);

  if (libraryQuery.isLoading && !library) {
    return <StatusState kind="loading" />;
  }

  if (libraryQuery.isError) {
    return (
      <StatusState
        kind="error"
        message="Your library could not be loaded."
        onRetry={() => void libraryQuery.refetch()}
        retrying={libraryQuery.isRefetching}
      />
    );
  }

  return (
    <Stack gap="var(--romd-page-gap)">
      <Stack gap="xs">
        <Title order={1} className="romd-page-heading">Browse games</Title>
        <Text c="dimmed">{filteredContext}</Text>
      </Stack>

      <Stack gap="sm">
        <Group gap="sm">
          <TextInput placeholder="Find games" aria-label="Search" leftSection={<IconSearch size={16} />} value={query} onChange={(event) => setQuery(event.currentTarget.value)} style={{ flex: '1 1 220px' }} />
          <Select aria-label="Sort" value={sortBy} onChange={(value) => updateFilter('sortBy', value)} data={sortOptions} allowDeselect={false} w={160} />
        </Group>
        <Box component="details">
          <Box component="summary" style={{ cursor: 'pointer' }}>Filters & view{activeFilters.length > 0 ? ` (${activeFilters.length})` : ''}</Box>
          <Group gap="sm" mt="sm" align="center">
            <Select aria-label="Platform" placeholder="All systems" clearable searchable value={systemKey} onChange={(value) => updateFilter('systemKey', value)} data={platforms.map(platform => ({ value: platform.id, label: platform.name }))} style={{ flex: '1 1 160px' }} />
            <Select aria-label="Genre" placeholder="All genres" clearable searchable value={genre} onChange={(value) => updateFilter('genre', value)} data={genres.map(facet => ({ value: facet.name, label: facet.name }))} style={{ flex: '1 1 160px' }} />
            <SegmentedControl aria-label="Release completeness" value={completeness} onChange={(value) => updateFilter('completeness', value)} data={completenessOptions} />
            <ActionIcon.Group>
              <ActionIcon variant={viewMode === 'grid' ? 'filled' : 'default'} aria-label="Grid view" onClick={() => changeViewMode('grid')} size="lg"><IconLayoutGrid size={18} /></ActionIcon>
              <ActionIcon variant={viewMode === 'list' ? 'filled' : 'default'} aria-label="List view" onClick={() => changeViewMode('list')} size="lg"><IconList size={18} /></ActionIcon>
            </ActionIcon.Group>
          </Group>
        </Box>
      </Stack>

      {activeFilters.length > 0 && (
        <Group
          gap="xs"
          align="center"
        >
          <Text
            size="sm"
            c="dimmed"
          >
            {loadedResultCount} loaded
          </Text>
          {activeFilters.map((filter) => (
            <Pill
              key={filter.key}
              withRemoveButton
              onRemove={filter.clear}
            >
              {filter.label}
            </Pill>
          ))}
          <Button
            variant="subtle"
            color="gray"
            size="compact-sm"
            onClick={clearFilters}
          >
            Clear all
          </Button>
        </Group>
      )}

      {catalogQuery.isError ? (
        <StatusState
          kind="error"
          message="Catalog results could not be loaded."
          onRetry={() => void catalogQuery.refetch()}
          retrying={catalogQuery.isRefetching}
        />
      ) : loadedResultCount === 0 && catalogQuery.isLoading ? (
        <PosterGridSkeleton />
      ) : loadedResultCount === 0 && !catalogQuery.isLoading ? (
        <StatusState
          kind="empty"
          title="No games match your filters"
          message="Try clearing a filter or widening your search."
        />
      ) : viewMode === 'grid' ? (
        <SimpleGrid
          cols={{ base: 2, xs: 3, md: 4, xl: 6 }}
          spacing="lg"
        >
          {titles.map((title) => (
            <PosterCard
              key={title.id}
              title={title}
              showPlayAction={false}
            />
          ))}
        </SimpleGrid>
      ) : (
        <Stack gap="sm">
          {titles.map((title) => (
            <TitleListRow
              key={title.id}
              title={title}
            />
          ))}
        </Stack>
      )}

      {catalogQuery.hasNextPage && (
        <Group justify="center">
          <Button
            leftSection={<IconRefresh size={16} />}
            onClick={() => catalogQuery.fetchNextPage()}
            loading={catalogQuery.isFetchingNextPage}
            variant="light"
          >
            Load more
          </Button>
        </Group>
      )}
    </Stack>
  );
}

const SKELETON_PLACEHOLDERS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

function PosterGridSkeleton() {
  return (
    <SimpleGrid
      cols={{ base: 2, xs: 3, md: 4, xl: 6 }}
      spacing="lg"
      aria-hidden
    >
      {SKELETON_PLACEHOLDERS.map((placeholder) => (
        <Stack
          key={placeholder}
          gap={10}
        >
          <Skeleton
            radius="lg"
            style={{ aspectRatio: 'var(--romd-poster-aspect)' }}
          />
          <Skeleton
            height={10}
            width="70%"
            radius="sm"
          />
          <Skeleton
            height={8}
            width="45%"
            radius="sm"
          />
        </Stack>
      ))}
    </SimpleGrid>
  );
}
