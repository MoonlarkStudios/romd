import { Box, Button, Group, Skeleton, Stack } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { EmptyState } from '../../components/EmptyState';
import classes from './Catalog.module.css';
import { CatalogCard } from './CatalogCard';
import { CatalogRow } from './CatalogRow';

export type CatalogView = 'list' | 'grid';

interface CatalogGridProps {
  titles: CatalogTitle[];
  view: CatalogView;
  isLoading?: boolean;
  hasMore?: boolean;
  onLoadMore?: () => void;
  isLoadingMore?: boolean;
  hasFilters?: boolean;
  selectable?: boolean;
  selectedIds?: Set<string>;
  onToggleSelected?: (id: string) => void;
}

function CatalogSkeleton({ view }: { view: CatalogView }) {
  if (view === 'list') {
    return (
      <Stack gap="xs">
        {Array.from({ length: 12 }).map((_, i) => (
          <Skeleton key={i} h={56} radius="sm" />
        ))}
      </Stack>
    );
  }
  return (
    <Box className={classes.grid}>
      {Array.from({ length: 18 }).map((_, i) => (
        <Skeleton key={i} h={300} radius="md" />
      ))}
    </Box>
  );
}

export function CatalogGrid({
  titles,
  view,
  isLoading = false,
  hasMore = false,
  onLoadMore,
  isLoadingMore = false,
  hasFilters = false,
  selectable = false,
  selectedIds,
  onToggleSelected,
}: CatalogGridProps) {
  if (isLoading) {
    return <CatalogSkeleton view={view} />;
  }

  if (titles.length === 0) {
    return (
      <EmptyState
        title={hasFilters ? 'No titles found' : 'No titles'}
        description={
          hasFilters
            ? 'Try adjusting your search terms or filters.'
            : 'Import a DAT file to populate your catalog.'
        }
      />
    );
  }

  return (
    <Stack gap="xl">
      {view === 'list' ? (
        <table className={classes.table} data-testid="catalog-list" aria-label="Catalog titles">
          <thead><tr>{selectable && <th className={classes.selection}><span aria-label="Selection" /></th>}<th>Title</th><th className={classes.platform}>System</th><th className={classes.year}>Year</th><th className={classes.availability}>Availability</th><th className={classes.status}>Metadata</th><th className={classes.artwork}>Artwork</th>{selectable && <th className={classes.actions}>Actions</th>}</tr></thead>
          <tbody>
          {titles.map((title) => (
            <CatalogRow
              key={title.id}
              title={title}
              selectable={selectable}
              selected={selectedIds?.has(title.id) ?? false}
              onToggleSelected={onToggleSelected}
            />
          ))}
          </tbody>
        </table>
      ) : (
        <Box className={classes.grid} data-testid="catalog-grid">
          {titles.map((title) => (
            <CatalogCard
              key={title.id}
              title={title}
              selectable={selectable}
              selected={selectedIds?.has(title.id) ?? false}
              onToggleSelected={onToggleSelected}
            />
          ))}
        </Box>
      )}

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
