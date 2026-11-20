import { Box, Button, Group, Skeleton, Stack, VisuallyHidden } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { type MouseEvent, type ReactNode, useMemo } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { EmptyState } from '../../components/EmptyState';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import classes from './Catalog.module.css';
import { CatalogCard } from './CatalogCard';
import { type CatalogPreview, CatalogRow } from './CatalogRow';
import { rememberCatalogPosition } from './catalogNavigation';

export type CatalogView = 'list' | 'grid';

interface CatalogGridProps {
  titles: CatalogTitle[];
  selectAll?: ReactNode;
  selectionDisabled?: boolean;
  onResetFilters?: () => void;
  view: CatalogView;
  isLoading?: boolean;
  hasMore?: boolean;
  onLoadMore?: () => void;
  isLoadingMore?: boolean;
  hasFilters?: boolean;
  selectable?: boolean;
  selectedIds?: Set<string>;
  onToggleSelected?: (id: string) => void;
  preview?: CatalogPreview;
}

function CatalogSkeleton({ view }: { view: CatalogView }) {
  if (view === 'list') {
    return (
      <Stack gap="xs">
        {Array.from(
          {
            length: 12,
          },
          (_, index) => `placeholder-${index}`,
        ).map((placeholderId) => (
          <Skeleton
            key={placeholderId}
            h={56}
            radius="sm"
          />
        ))}
      </Stack>
    );
  }
  return (
    <Box className={classes.grid}>
      {Array.from(
        {
          length: 18,
        },
        (_, index) => `placeholder-${index}`,
      ).map((placeholderId) => (
        <Skeleton
          key={placeholderId}
          h={300}
          radius="md"
        />
      ))}
    </Box>
  );
}

export function CatalogGrid({
  titles,
  selectAll,
  selectionDisabled,
  onResetFilters,
  view,
  isLoading = false,
  hasMore = false,
  onLoadMore,
  isLoadingMore = false,
  hasFilters = false,
  selectable = false,
  selectedIds,
  onToggleSelected,
  preview,
}: CatalogGridProps) {
  const { pathname, search } = useLocation();
  const navigate = useNavigate();
  const { data: platforms } = usePlatforms();
  const systemNames = useMemo(
    () =>
      new Map(
        platforms?.map((item) => [
          item.key,
          item.compactLabel || item.name,
        ]),
      ),
    [
      platforms,
    ],
  );

  // Keep memoized result rows independent of URL changes. The anchors still support normal
  // browser link actions; this handler adds SPA navigation context to an ordinary activation.
  const activateResult = (event: MouseEvent<HTMLElement>) => {
    if (
      event.defaultPrevented ||
      !(event.target instanceof Element) ||
      !event.currentTarget.contains(event.target)
    )
      return;
    const anchor = event.target.closest<HTMLAnchorElement>('a[data-catalog-title-link]');
    if (anchor) {
      const returnTo = `${pathname}${search}`;
      if (!rememberCatalogPosition(returnTo, event)) return;
      const href = anchor.getAttribute('href');
      if (!href) return;
      event.preventDefault();
      void navigate(href, {
        state: {
          catalogReturnTo: returnTo,
        },
      });
      return;
    }
    const trigger = event.target.closest<HTMLElement>('[data-inspector-trigger]');
    const id = trigger?.closest<HTMLElement>('[data-inspector-row]')?.dataset.inspectorRow;
    if (!id || !preview) return;
    if (preview.selectedId === id) preview.close();
    else preview.select(id, trigger ?? undefined);
  };

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
      >
        {hasFilters && onResetFilters && (
          <Button
            {...workspaceActionProps}
            variant="light"
            onClick={onResetFilters}
          >
            Reset search and filters
          </Button>
        )}
      </EmptyState>
    );
  }

  return (
    <Stack
      gap="xl"
      onKeyDown={preview?.onListKeyDown}
      onClick={activateResult}
    >
      {view === 'list' ? (
        <table
          className={classes.table}
          data-testid="catalog-list"
          aria-label="Catalog titles"
        >
          <thead>
            <tr>
              {selectable && (
                <th className={classes.selection}>
                  {selectAll ?? <VisuallyHidden>Selection</VisuallyHidden>}
                </th>
              )}
              <th>Title</th>
              <th className={classes.platform}>System</th>
              <th className={classes.year}>Year</th>
              <th className={classes.availability}>Availability</th>
              <th className={classes.status}>Enrichment</th>
              <th className={classes.artwork}>Artwork</th>
              {selectable && <th className={classes.actions}>Actions</th>}
            </tr>
          </thead>
          <tbody>
            {titles.map((title) => (
              <CatalogRow
                key={title.id}
                title={title}
                selectable={selectable}
                selectionDisabled={selectionDisabled}
                selected={selectedIds?.has(title.id) ?? false}
                onToggleSelected={onToggleSelected}
                showPreview={!!preview}
                previewed={preview?.selectedId === title.id}
                systemName={systemNames.get(title.systemKey)}
              />
            ))}
          </tbody>
        </table>
      ) : (
        <Box
          className={classes.grid}
          data-testid="catalog-grid"
        >
          {titles.map((title) => (
            <CatalogCard
              key={title.id}
              title={title}
              selectable={selectable}
              selectionDisabled={selectionDisabled}
              selected={selectedIds?.has(title.id) ?? false}
              onToggleSelected={onToggleSelected}
              showPreview={!!preview}
              previewed={preview?.selectedId === title.id}
              systemName={systemNames.get(title.systemKey)}
            />
          ))}
        </Box>
      )}

      {hasMore && (
        <Group justify="center">
          <Button
            variant="light"
            onClick={onLoadMore}
            loading={isLoadingMore}
          >
            Load More
          </Button>
        </Group>
      )}
    </Stack>
  );
}
