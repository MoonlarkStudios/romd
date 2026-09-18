import { ActionIcon, Alert, Button, Checkbox, Group, Select, Stack, Text, TextInput, Title, Tooltip } from '@mantine/core';
import { useDisclosure, useLocalStorage } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconBookmark, IconBookmarkOff, IconLayoutGrid, IconList, IconPhotoPlus, IconSearch, IconSparkles } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { SelectionToolbar } from '../../components/Workspace/SelectionToolbar';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useFillMissingArtwork } from '../../hooks/api/useArtworkAcquisition';
import { useCatalogSearch } from '../../hooks/api/useCatalogSearch';
import { useSetTitlesTracking } from '../../hooks/api/useTitleActions';
import { usePermissions } from '../../hooks/usePermissions';
import { BatchEnrichmentModal } from './BatchEnrichmentModal';
import classes from './Catalog.module.css';
import { ActiveCatalogFilters, CatalogFilters } from './CatalogFilters';
import { CatalogGrid, type CatalogView } from './CatalogGrid';
import { UntrackConfirmation } from './CatalogTrackingAction';
import { type LockedCatalogFilters, useCatalogFilters } from './useCatalogFilters';

export interface LockedCatalogFilterLabels { systemKey?: string; tracked?: string }
interface TitlesLensProps { title?: string; showHeader?: boolean; lockedFilters?: LockedCatalogFilters; lockedFilterLabels?: LockedCatalogFilterLabels; browseAllHref?: string }

export function TitlesLens({ title = 'Catalog', showHeader = true, lockedFilters, lockedFilterLabels, browseAllHref = '/catalog?view=all' }: TitlesLensProps) {
  const filters = useCatalogFilters({ lockedFilters });
  const { canManageTitles } = usePermissions();
  const [batchOpened, { open: openBatch, close: closeBatch }] = useDisclosure(false);
  const [confirmUntrack, setConfirmUntrack] = useState(false);
  const mutation = useSetTitlesTracking();
  const artwork = useFillMissingArtwork();
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [savedView, setView] = useLocalStorage<string>({ key: 'romd-catalog-view', defaultValue: 'list' });
  const view: CatalogView = savedView === 'list' ? 'list' : 'grid';
  const filterIdentity = JSON.stringify(filters.filters);
  useEffect(() => { setSelectedIds(new Set()); setConfirmUntrack(false); }, [filterIdentity]);
  const query = useCatalogSearch({
    query: filters.filters.query || undefined, systemKey: filters.filters.systemKey,
    genre: filters.filters.genre, enrichmentStatus: filters.filters.enrichmentStatus,
    releaseCompleteness: filters.filters.releaseCompleteness, tracked: filters.filters.tracked, sortBy: filters.filters.sortBy,
  });
  const titles = query.data?.pages.flatMap((page) => page.items) ?? [];
  const selected = titles.filter((item) => selectedIds.has(item.id));
  const toggle = (id: string) => setSelectedIds((previous) => { const next = new Set(previous); if (next.has(id)) next.delete(id); else next.add(id); return next; });
  const setTracked = (tracked: boolean) => {
    const titleIds = selected.filter((item) => !!item.isTracked !== tracked).map((item) => item.id);
    if (!titleIds.length) return;
    mutation.mutate({ titleIds, tracked }, {
      onSuccess: () => { setSelectedIds(new Set()); setConfirmUntrack(false); notifications.show({ message: `${titleIds.length} ${titleIds.length === 1 ? 'title' : 'titles'} ${tracked ? 'tracked' : 'untracked'}`, color: 'teal' }); },
      onError: () => notifications.show({ message: 'Could not update tracking. Try again.', color: 'red' }),
    });
  };
  const hasFilters = !!filters.filters.query || !!filters.filters.genre || !!filters.filters.enrichmentStatus || filters.filters.releaseCompleteness !== 'all' || (!lockedFilters?.systemKey && !!filters.filters.systemKey) || (!lockedFilters?.tracked && filters.filters.tracked !== 'all');
  return <Stack gap="sm">
    {showHeader && <Title order={2} className={classes.heading}>{title}</Title>}
    <div className={classes.toolbar}>
      <TextInput className={classes.search} aria-label="Search titles" placeholder="Search titles..." leftSection={<IconSearch size={17} />} value={filters.searchInput} onChange={(event) => filters.setSearchInput(event.currentTarget.value)} />
      <CatalogFilters filters={filters} lockedFilterLabels={lockedFilterLabels} />
    </div>
    <ActiveCatalogFilters filters={filters} />
    <Group justify="space-between" gap="xs">
      <Group gap="sm">
        {canManageTitles && titles.length > 0 && <Checkbox aria-label="Select loaded titles" checked={selected.length === titles.length} indeterminate={selected.length > 0 && selected.length < titles.length} onChange={(event) => setSelectedIds(event.currentTarget.checked ? new Set(titles.map((item) => item.id)) : new Set())} />}
        <Text size="xs" c="dimmed">{query.isLoading ? 'Loading titles' : `${titles.length}${query.hasNextPage ? '+' : ''} titles`}</Text>
      </Group>
      <Group gap="xs">
        <Select aria-label="Sort titles" value={filters.filters.sortBy} onChange={(value) => filters.setFilter('sortBy', value)} data={[{ value: 'name', label: 'Name (A-Z)' }, { value: 'rating', label: 'Rating' }, { value: 'relevance', label: 'Relevance', disabled: !filters.filters.query }]} w={150} size="xs" />
        <Group gap={2} role="group" aria-label="Catalog layout">
          <Tooltip label="List"><ActionIcon aria-label="List view" aria-pressed={view === 'list'} variant={view === 'list' ? 'light' : 'subtle'} color={view === 'list' ? 'teal' : 'gray'} onClick={() => setView('list')}><IconList size={18} /></ActionIcon></Tooltip>
          <Tooltip label="Grid"><ActionIcon aria-label="Grid view" aria-pressed={view === 'grid'} variant={view === 'grid' ? 'light' : 'subtle'} color={view === 'grid' ? 'teal' : 'gray'} onClick={() => setView('grid')}><IconLayoutGrid size={18} /></ActionIcon></Tooltip>
        </Group>
      </Group>
    </Group>
    {canManageTitles && selected.length > 0 && <SelectionToolbar count={selected.length} disabled={mutation.isPending} onClear={() => setSelectedIds(new Set())}>
        {selected.some((item) => !item.isTracked) && <Button {...workspaceActionProps} size="xs" variant="light" leftSection={<IconBookmark size={15} />} loading={mutation.isPending} onClick={() => setTracked(true)}>Track</Button>}
        {selected.some((item) => item.isTracked) && <Button size="xs" variant="default" leftSection={<IconBookmarkOff size={15} />} disabled={mutation.isPending} onClick={() => setConfirmUntrack(true)}>Untrack</Button>}
        <Button {...workspaceActionProps} size="xs" variant="light" leftSection={<IconSparkles size={15} />} onClick={openBatch}>Enrich</Button>
        {selected.some((item) => item.isTracked) && <Button {...workspaceActionProps} size="xs" variant="light" leftSection={<IconPhotoPlus size={15} />} loading={artwork.isPending} onClick={() => artwork.mutate(selected.filter((item) => item.isTracked).map((item) => item.id), {
          onSuccess: (result) => notifications.show({ message: `${result.queued} artwork requests queued${result.failed.length ? `; ${result.failed.length} could not be queued` : ''}`, color: result.failed.length ? 'orange' : 'teal' }),
        })}>Fill missing artwork</Button>}
    </SelectionToolbar>}
    {query.isError ? <Alert color="red" title="Could not load catalog"><Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert> :
      !query.isLoading && !titles.length && !hasFilters && lockedFilters?.tracked === 'tracked' ?
      <Stack align="center" py={60} gap="xs"><Text fw={600}>No tracked titles yet</Text><Button component="a" href={browseAllHref} {...workspaceActionProps} variant="light">Browse all titles</Button></Stack> :
      <CatalogGrid titles={titles} view={view} isLoading={query.isLoading} hasMore={query.hasNextPage} onLoadMore={() => void query.fetchNextPage()} isLoadingMore={query.isFetchingNextPage} hasFilters={hasFilters} selectable={canManageTitles} selectedIds={selectedIds} onToggleSelected={toggle} />}
    <UntrackConfirmation opened={confirmUntrack} count={selected.filter((item) => item.isTracked).length} busy={mutation.isPending} error={mutation.isError ? 'Could not untrack titles. Try again.' : null} onClose={() => { setConfirmUntrack(false); mutation.reset(); }} onConfirm={() => setTracked(false)} />
    {canManageTitles && <BatchEnrichmentModal opened={batchOpened} onClose={closeBatch} eligibleTitles={selected} />}
  </Stack>;
}
