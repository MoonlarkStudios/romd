import {
  ActionIcon,
  Alert,
  Button,
  Checkbox,
  CloseButton,
  Group,
  Loader,
  Select,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { useElementSize, useLocalStorage } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import {
  IconBookmark,
  IconBookmarkOff,
  IconLayoutGrid,
  IconList,
  IconSearch,
  IconX,
} from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCatalogCount, useCatalogSearch } from '../../hooks/api/useCatalogSearch';
import { useSetTitlesTracking } from '../../hooks/api/useTitleActions';
import { usePermissions } from '../../hooks/usePermissions';
import classes from './Catalog.module.css';
import { ActiveCatalogFilters, CatalogFilters } from './CatalogFilters';
import { CatalogGrid, type CatalogView } from './CatalogGrid';
import { UntrackConfirmation } from './CatalogTrackingAction';
import { type LockedCatalogFilters, useCatalogFilters } from './useCatalogFilters';

export interface LockedCatalogFilterLabels {
  systemKey?: string;
  tracked?: string;
}
interface TitlesLensProps {
  title?: string;
  showHeader?: boolean;
  lockedFilters?: LockedCatalogFilters;
  lockedFilterLabels?: LockedCatalogFilterLabels;
  browseAllHref?: string;
}

export function TitlesLens({
  title = 'Catalog',
  showHeader = true,
  lockedFilters,
  lockedFilterLabels,
  browseAllHref = '/catalog?view=all',
}: TitlesLensProps) {
  const { ref: toolbarRef, height: toolbarHeight } = useElementSize();
  const filters = useCatalogFilters({
    lockedFilters,
  });
  const { canManageTitles } = usePermissions();
  const [confirmUntrack, setConfirmUntrack] = useState(false);
  const mutation = useSetTitlesTracking();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const artworkJobId = searchParams.get('artworkJob');
  // Links to the retired catalog run view open that run in the enrichment workspace.
  useEffect(() => {
    if (canManageTitles && artworkJobId)
      navigate(`/enrichment?activity=${encodeURIComponent(artworkJobId)}`, {
        replace: true,
      });
  }, [
    artworkJobId,
    canManageTitles,
    navigate,
  ]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [savedView, setView] = useLocalStorage<string>({
    key: 'romd-catalog-view',
    defaultValue: 'list',
  });
  const view: CatalogView = savedView === 'list' ? 'list' : 'grid';
  const filterIdentity = JSON.stringify(filters.filters);
  const previousFilterIdentity = useRef(filterIdentity);
  useEffect(() => {
    if (previousFilterIdentity.current === filterIdentity) return;
    previousFilterIdentity.current = filterIdentity;
    setSelectedIds(new Set());
    setConfirmUntrack(false);
  }, [
    filterIdentity,
  ]);
  const catalogParams = {
    query: filters.filters.query || undefined,
    systemKey: filters.filters.systemKey,
    artworkStatus: filters.filters.artworkStatus,
    genre: filters.filters.genre,
    enrichment: filters.filters.enrichment,
    enrichmentStatus: filters.filters.enrichmentStatus,
    releaseReview:
      filters.filters.releaseReview === 'all' ? undefined : filters.filters.releaseReview,
    releaseCompleteness: filters.filters.releaseCompleteness,
    tracked: filters.filters.tracked,
    sortBy: filters.filters.sortBy,
  };
  const query = useCatalogSearch(catalogParams);
  const count = useCatalogCount(catalogParams);
  const titles = query.data?.pages.flatMap((page) => page.items) ?? [];
  const totalCount = count.data;
  const resultLabel =
    totalCount != null
      ? `${titles.length.toLocaleString()} of ${totalCount.toLocaleString()} titles`
      : `${titles.length} shown${count.isPending ? ' · counting…' : ''}`;
  const selected = titles.filter((item) => selectedIds.has(item.id));
  const updating = query.isPlaceholderData || filters.searchInput !== filters.filters.query;
  const toggle = (id: string) => {
    if (updating || mutation.isPending) return;
    setSelectedIds((previous) => {
      const next = new Set(previous);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };
  const setTracked = (tracked: boolean) => {
    const titleIds = selected.filter((item) => !!item.isTracked !== tracked).map((item) => item.id);
    if (!titleIds.length) return;
    mutation.mutate(
      {
        titleIds,
        tracked,
      },
      {
        onSuccess: () => {
          setSelectedIds(new Set());
          setConfirmUntrack(false);
          notifications.show({
            message: `${titleIds.length} ${titleIds.length === 1 ? 'title' : 'titles'} ${tracked ? 'tracked' : 'untracked'}`,
            color: 'teal',
          });
        },
        onError: () =>
          notifications.show({
            message: 'Could not update tracking. Try again.',
            color: 'red',
          }),
      },
    );
  };
  const hasFilters =
    filters.filters.releaseReview !== 'all' ||
    !!filters.filters.artworkStatus ||
    !!filters.filters.query ||
    !!filters.filters.genre ||
    filters.filters.enrichment.length > 0 ||
    !!filters.filters.enrichmentStatus ||
    filters.filters.releaseCompleteness !== 'all' ||
    (!lockedFilters?.systemKey && !!filters.filters.systemKey) ||
    (!lockedFilters?.tracked && filters.filters.tracked !== 'all');
  const selectAll = (
    <Checkbox
      aria-label="Select loaded titles"
      disabled={updating || mutation.isPending || !titles.length}
      checked={titles.length > 0 && selected.length === titles.length}
      indeterminate={selected.length > 0 && selected.length < titles.length}
      onChange={(event) =>
        setSelectedIds(
          event.currentTarget.checked ? new Set(titles.map((item) => item.id)) : new Set(),
        )
      }
    />
  );
  return (
    <Stack
      gap="sm"
      style={{
        '--catalog-toolbar-height': `${toolbarHeight}px`,
      }}
    >
      {showHeader && (
        <Title
          order={2}
          className={classes.heading}
        >
          {title}
        </Title>
      )}
      <div className={classes.toolbar}>
        <TextInput
          className={classes.search}
          label="Search titles"
          placeholder="Search by title name…"
          rightSection={
            filters.searchInput ? (
              <CloseButton
                aria-label="Clear search"
                onClick={() => filters.setSearchInput('')}
                size="sm"
              />
            ) : null
          }
          leftSection={
            (query.isFetching && !query.isFetchingNextPage) || updating ? (
              <Loader
                size="xs"
                aria-label="Updating titles"
              />
            ) : (
              <IconSearch size={17} />
            )
          }
          value={filters.searchInput}
          onChange={(event) => filters.setSearchInput(event.currentTarget.value)}
        />
        <CatalogFilters
          filters={filters}
          lockedFilterLabels={lockedFilterLabels}
        />
      </div>
      <ActiveCatalogFilters filters={filters} />
      <div
        ref={toolbarRef}
        className={classes.stickyToolbar}
      >
        <div className={classes.resultsToolbar}>
          <Group
            gap="sm"
            wrap="nowrap"
          >
            {canManageTitles && view === 'grid' && selectAll}
            <Text
              className={classes.resultCount}
              size="xs"
              c="dimmed"
              aria-live="polite"
            >
              {query.isLoading ? 'Loading titles' : updating ? 'Updating titles…' : resultLabel}
            </Text>
            {count.isError && (
              <Button
                size="compact-xs"
                variant="subtle"
                color="gray"
                onClick={() => void count.refetch()}
              >
                Retry count
              </Button>
            )}
          </Group>
          {canManageTitles && (
            <Group
              className={classes.selectionActions}
              data-empty={!selected.length || undefined}
              gap="xs"
              wrap="nowrap"
              role="region"
              aria-label="Selection actions"
              aria-hidden={!selected.length}
            >
              <Text
                size="xs"
                className={classes.selectionCount}
                aria-live="polite"
              >
                {selected.length} selected
              </Text>
              {lockedFilters?.tracked !== 'tracked' && (
                <Button
                  {...workspaceActionProps}
                  size="xs"
                  variant="light"
                  leftSection={<IconBookmark size={15} />}
                  disabled={
                    !selected.some((item) => !item.isTracked) || mutation.isPending || updating
                  }
                  onClick={() => setTracked(true)}
                >
                  Track
                </Button>
              )}
              <Button
                size="xs"
                variant="default"
                leftSection={<IconBookmarkOff size={15} />}
                disabled={
                  !selected.some((item) => item.isTracked) || mutation.isPending || updating
                }
                onClick={() => setConfirmUntrack(true)}
              >
                Untrack
              </Button>
              <Tooltip label="Clear selection">
                <ActionIcon
                  aria-label="Clear selection"
                  variant="subtle"
                  color="gray"
                  disabled={!selected.length || mutation.isPending}
                  onClick={() => setSelectedIds(new Set())}
                >
                  <IconX size={16} />
                </ActionIcon>
              </Tooltip>
            </Group>
          )}

          <Group
            gap="xs"
            wrap="nowrap"
            className={classes.viewControls}
          >
            <Select
              aria-label="Sort titles"
              value={filters.filters.sortBy}
              onChange={(value) => filters.setFilter('sortBy', value)}
              data={[
                {
                  value: 'name',
                  label: 'Name (A-Z)',
                },
                {
                  value: 'rating',
                  label: 'Rating',
                },
                {
                  value: 'relevance',
                  label: 'Relevance',
                  disabled: !filters.filters.query,
                },
              ]}
              w={150}
              size="xs"
            />
            <Group
              gap={2}
              role="group"
              aria-label="Catalog layout"
            >
              <Tooltip label="List">
                <ActionIcon
                  aria-label="List view"
                  aria-pressed={view === 'list'}
                  variant={view === 'list' ? 'light' : 'subtle'}
                  color={view === 'list' ? 'teal' : 'gray'}
                  onClick={() => setView('list')}
                >
                  <IconList size={18} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Grid">
                <ActionIcon
                  aria-label="Grid view"
                  aria-pressed={view === 'grid'}
                  variant={view === 'grid' ? 'light' : 'subtle'}
                  color={view === 'grid' ? 'teal' : 'gray'}
                  onClick={() => setView('grid')}
                >
                  <IconLayoutGrid size={18} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Group>
        </div>
      </div>
      {query.isError ? (
        <Alert
          color="red"
          title="Could not load catalog"
        >
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      ) : !query.isLoading &&
        !titles.length &&
        !hasFilters &&
        lockedFilters?.tracked === 'tracked' ? (
        <Stack
          align="center"
          py={60}
          gap="xs"
        >
          <Text fw={600}>No tracked titles yet</Text>
          <Button
            component="a"
            href={browseAllHref}
            {...workspaceActionProps}
            variant="light"
          >
            Browse all titles
          </Button>
        </Stack>
      ) : (
        <CatalogGrid
          selectAll={selectAll}
          onResetFilters={filters.clearFilters}
          selectionDisabled={updating || mutation.isPending}
          titles={titles}
          view={view}
          isLoading={query.isLoading}
          hasMore={!updating && query.hasNextPage}
          onLoadMore={() => void query.fetchNextPage()}
          isLoadingMore={query.isFetchingNextPage}
          hasFilters={hasFilters}
          selectable={canManageTitles}
          selectedIds={selectedIds}
          onToggleSelected={toggle}
        />
      )}
      <UntrackConfirmation
        opened={confirmUntrack}
        count={selected.filter((item) => item.isTracked).length}
        busy={mutation.isPending}
        error={mutation.isError ? 'Could not untrack titles. Try again.' : null}
        onClose={() => {
          setConfirmUntrack(false);
          mutation.reset();
        }}
        onConfirm={() => setTracked(false)}
      />
    </Stack>
  );
}
