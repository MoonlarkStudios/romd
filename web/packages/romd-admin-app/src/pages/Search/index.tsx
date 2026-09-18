import { Group, Select, Stack, Text, TextInput, Title } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';
import { useGameSearch } from '../../hooks/api/useGameSearch';
import { SearchFilters } from './SearchFilters';
import { SearchResults } from './SearchResults';
import { useSearchFilters } from './useSearchFilters';

const sortOptions = [
  { value: 'name', label: 'Name (A-Z)' },
  { value: 'year', label: 'Year (Newest)' },
  { value: 'relevance', label: 'Relevance' },
];

/**
 * Global game search page with full-text search, filters, and pagination.
 */
export function Search() {
  const filters = useSearchFilters();

  const { data, fetchNextPage, hasNextPage, isFetchingNextPage, isLoading, isError } =
    useGameSearch({
      query: filters.filters.query || undefined,
      systemKey: filters.filters.systemKey,
      year: filters.filters.year,
      manufacturer: filters.filters.manufacturer,
      regionId: filters.filters.region,
      bios: filters.filters.bios,
      sortBy: filters.filters.sortBy,
    });

  const games = data?.pages.flatMap((page) => page.items) ?? [];
  const totalCount = data?.pages[0]?.items.length ? games.length : 0;

  // Disable relevance sort when no query is present (backend returns 400)
  const sortOptionsWithDisabled = sortOptions.map((opt) => ({
    ...opt,
    disabled: opt.value === 'relevance' && !filters.filters.query,
  }));

  return (
    <Stack gap="lg">
      <Title order={2}>Search Games</Title>

      {/* Search Input */}
      <TextInput
        placeholder="Search games by name..."
        leftSection={<IconSearch size={18} />}
        value={filters.searchInput}
        onChange={(e) => filters.setSearchInput(e.target.value)}
        size="md"
      />

      {/* Filters Row */}
      <Group justify="space-between" align="flex-end">
        <SearchFilters filters={filters} />

        <Group gap="md">
          {/* Result Count */}
          {!isLoading && games.length > 0 && (
            <Text size="sm" c="dimmed">
              {totalCount} {totalCount === 1 ? 'result' : 'results'}
              {hasNextPage && '+'}
            </Text>
          )}

          {/* Sort Control */}
          <Select
            value={filters.filters.sortBy}
            onChange={(v) => filters.setFilter('sortBy', v)}
            data={sortOptionsWithDisabled}
            w={160}
            size="sm"
          />
        </Group>
      </Group>

      {/* Error State */}
      {isError && (
        <Text c="red" size="sm">
          Failed to search games. Please try again.
        </Text>
      )}

      {/* Results */}
      <SearchResults
        games={games}
        isLoading={isLoading}
        hasMore={hasNextPage}
        onLoadMore={() => fetchNextPage()}
        isLoadingMore={isFetchingNextPage}
        hasFilters={filters.hasActiveFilters}
      />
    </Stack>
  );
}
