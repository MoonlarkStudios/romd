import { Button, Group, Select, TextInput } from '@mantine/core';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import type { UseSearchFiltersReturn } from './useSearchFilters';

interface SearchFiltersProps {
  filters: UseSearchFiltersReturn;
}

/**
 * Filter controls for the game search page.
 * Includes platform, year, BIOS filter, and clear button.
 */
export function SearchFilters({ filters }: SearchFiltersProps) {
  const { data: platforms, isLoading: platformsLoading } = usePlatforms();

  const platformOptions =
    platforms?.map((p) => ({
      value: p.key,
      label: p.name,
    })) ?? [];

  const biosOptions = [
    { value: 'exclude', label: 'Exclude BIOS' },
    { value: 'include', label: 'Include BIOS' },
    { value: 'only', label: 'BIOS Only' },
  ];

  return (
    <Group>
      <Select
        placeholder="Platform"
        data={platformOptions}
        value={filters.filters.systemKey ?? null}
        onChange={(v) => filters.setFilter('systemKey', v)}
        clearable
        searchable
        disabled={platformsLoading}
        w={200}
      />
      <TextInput
        placeholder="Year"
        value={filters.filters.year ?? ''}
        onChange={(e) => filters.setFilter('year', e.target.value || null)}
        w={100}
      />
      <Select
        placeholder="BIOS"
        data={biosOptions}
        value={filters.filters.bios}
        onChange={(v) => filters.setFilter('bios', v)}
        w={150}
      />
      {filters.hasActiveFilters && (
        <Button variant="subtle" onClick={filters.clearFilters}>
          Clear Filters
        </Button>
      )}
    </Group>
  );
}
