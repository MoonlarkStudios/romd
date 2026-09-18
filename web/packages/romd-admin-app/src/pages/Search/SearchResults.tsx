import { Badge, Button, Group, Skeleton, Stack, Text } from '@mantine/core';
import type { DatGame } from '@romd/admin-api-client';
import { useMemo } from 'react';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn } from '../../components/DataTable/types';
import { EmptyState } from '../../components/EmptyState';

interface SearchResultsProps {
  /** Game data to display */
  games: DatGame[];
  /** Loading state for initial load */
  isLoading?: boolean;
  /** Whether more pages are available */
  hasMore?: boolean;
  /** Handler for Load More button */
  onLoadMore?: () => void;
  /** Whether more data is being loaded */
  isLoadingMore?: boolean;
  /** Whether results are filtered */
  hasFilters?: boolean;
}

/**
 * Search results table with Load More pagination.
 */
export function SearchResults({
  games,
  isLoading = false,
  hasMore = false,
  onLoadMore,
  isLoadingMore = false,
  hasFilters = false,
}: SearchResultsProps) {
  const columns = useMemo<DataTableColumn<DatGame>[]>(
    () => [
      {
        id: 'name',
        header: 'Name',
        accessorKey: 'name',
        enableSorting: false,
        cell: ({ getValue }) => (
          <Text fw={500} size="sm" truncate="end" maw={400}>
            {getValue() as string}
          </Text>
        ),
      },
      {
        id: 'year',
        header: 'Year',
        accessorKey: 'year',
        enableSorting: false,
        width: 80,
        cell: ({ getValue }) => (
          <Text size="sm" c="dimmed">
            {(getValue() as string | null) ?? '-'}
          </Text>
        ),
      },
      {
        id: 'manufacturer',
        header: 'Manufacturer',
        accessorKey: 'manufacturer',
        enableSorting: false,
        cell: ({ getValue }) => (
          <Text size="sm" truncate="end" maw={200}>
            {(getValue() as string | null) ?? '-'}
          </Text>
        ),
      },
      {
        id: 'isBios',
        header: 'Type',
        accessorKey: 'isBios',
        enableSorting: false,
        width: 80,
        cell: ({ getValue }) =>
          getValue() ? (
            <Badge variant="light" size="sm" color="grape">
              BIOS
            </Badge>
          ) : null,
      },
      {
        id: 'roms',
        header: 'ROMs',
        enableSorting: false,
        align: 'right',
        width: 80,
        cell: ({ row }) => (
          <Text size="sm" c="dimmed">
            {row.original.roms?.length ?? 0}
          </Text>
        ),
      },
    ],
    []
  );

  if (isLoading) {
    return (
      <Stack gap="sm">
        {Array.from({ length: 10 }).map((_, i) => (
          <Skeleton key={i} height={40} />
        ))}
      </Stack>
    );
  }

  if (games.length === 0) {
    return (
      <EmptyState
        title={hasFilters ? 'No games found' : 'No games'}
        description={
          hasFilters
            ? 'Try adjusting your search terms or filters.'
            : 'Search for games by name or apply filters.'
        }
      />
    );
  }

  return (
    <Stack gap="md">
      <DataTable
        data={games}
        columns={columns}
        getRowId={(row) => row.id}
        enableSorting={false}
        enableColumnVisibility={false}
        enablePagination={false}
        striped
        highlightOnHover
      />

      {hasMore && (
        <Group justify="center">
          <Button variant="light" onClick={onLoadMore} loading={isLoadingMore}>
            Load More
          </Button>
        </Group>
      )}
    </Stack>
  );
}
