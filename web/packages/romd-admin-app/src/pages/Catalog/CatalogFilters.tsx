import { Badge, Button, Drawer, Group, Pill, Select, Stack, Text } from '@mantine/core';
import { IconAdjustmentsHorizontal, IconArrowLeft } from '@tabler/icons-react';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { useManagedSystems } from '../../hooks/api/useManagedSystems';
import { MissingTrackedTitlesList, TrackedTitleUpgradesList } from './TrackedTitlesList';
import type { ReleaseCompletenessFilter, TrackedFilter, UseCatalogFiltersReturn } from './useCatalogFilters';

const GENRES = ['Action', 'Adventure', 'Arcade', 'Fighting', 'Platform', 'Puzzle', 'Racing', 'RPG', 'Shooter', 'Simulation', 'Sports', 'Strategy'];
const STATUSES = [{ value: 'None', label: 'Not enriched' }, { value: 'Completed', label: 'Enriched' }, { value: 'Failed', label: 'Failed' }, { value: 'NotFound', label: 'No match' }, { value: 'Pending', label: 'Pending' }, { value: 'LowConfidence', label: 'Needs match review' }];
const COMPLETENESS = [{ value: 'all', label: 'Any' }, { value: 'complete', label: 'Files present for every DAT release' }, { value: 'partial', label: 'Files present for some DAT releases' }, { value: 'none', label: 'No files for DAT releases' }];

interface Props { filters: UseCatalogFiltersReturn; lockedFilterLabels?: { systemKey?: string; tracked?: string }; showShortcuts?: boolean }
export function CatalogFilters({ filters, lockedFilterLabels }: Props) {
  const { data: systems, isLoading, isError } = useManagedSystems();
  const platforms = systems?.filter((system) => system.enabled);
  const [params, setParams] = useSearchParams();
  const legacyReview = params.get('view');
  const [review, setReview] = useState(legacyReview === 'missing' || legacyReview === 'upgrades' ? legacyReview : '');
  const [opened, setOpened] = useState(!!review);
  const count = Number(!!filters.filters.genre) + Number(!!filters.filters.enrichmentStatus) + Number(filters.filters.releaseCompleteness !== 'all') + Number(!filters.lockedFilters.tracked && filters.filters.tracked !== 'all');
  const close = () => {
    setOpened(false); setReview('');
    if (legacyReview === 'missing' || legacyReview === 'upgrades') setParams((previous) => { const next = new URLSearchParams(previous); next.set('view', 'tracked'); return next; }, { replace: true });
  };
  return <>
    {filters.lockedFilters.systemKey ? <Badge color="gray" variant="light">{lockedFilterLabels?.systemKey ?? platforms?.find((p) => p.key === filters.lockedFilters.systemKey)?.name ?? filters.lockedFilters.systemKey}</Badge> :
      <Select aria-label="System" placeholder="All systems" data={platforms?.map((p) => ({ value: p.key, label: p.name })) ?? []} value={filters.filters.systemKey ?? null} onChange={(v) => filters.setFilter('systemKey', v)} disabled={isLoading} error={isError ? 'Could not load systems' : undefined} nothingFoundMessage="No matching systems" clearable searchable w={200} />}
    <Button variant="default" leftSection={<IconAdjustmentsHorizontal size={16} />} onClick={() => setOpened(true)}>Filters{count ? ` (${count})` : ''}</Button>
    <Drawer opened={opened} onClose={close} position="right" title={review ? review === 'missing' ? 'Tracked without a complete release' : 'Preferred release missing' : 'Catalog filters'} size={review ? 'lg' : 'sm'}>
      {review ? <Stack><Button variant="subtle" leftSection={<IconArrowLeft size={16} />} onClick={() => setReview('')}>Filters</Button>{review === 'missing' ? <MissingTrackedTitlesList /> : <TrackedTitleUpgradesList />}</Stack> : <Stack gap="lg">
        <Select label="Genre" placeholder="Any genre" data={GENRES} value={filters.filters.genre ?? null} onChange={(v) => filters.setFilter('genre', v)} clearable searchable />
        <Select label="Metadata status" placeholder="Any status" data={STATUSES} value={filters.filters.enrichmentStatus ?? null} onChange={(v) => filters.setFilter('enrichmentStatus', v)} clearable />
        {!filters.lockedFilters.tracked && <Select label="Tracking" placeholder="Any title" data={[{ value: 'tracked', label: 'Tracked' }, { value: 'untracked', label: 'Untracked' }]} value={filters.filters.tracked === 'all' ? null : filters.filters.tracked} onChange={(v) => filters.setFilter('tracked', (v as TrackedFilter | null) ?? 'all')} clearable />}
        <Select label="DAT release coverage" data={COMPLETENESS} value={filters.filters.releaseCompleteness} onChange={(v) => filters.setFilter('releaseCompleteness', v as ReleaseCompletenessFilter)} />
        {filters.lockedFilters.tracked === 'tracked' && <Stack gap="xs"><Text size="sm" fw={600}>Tracked release review</Text><Button variant="default" onClick={() => setReview('missing')}>Without a complete release</Button><Button variant="default" onClick={() => setReview('upgrades')}>Preferred release missing</Button></Stack>}
        <Group justify="space-between"><Button variant="subtle" color="gray" onClick={filters.clearFilters}>Clear filters</Button><Button color="teal" onClick={close}>Done</Button></Group>
      </Stack>}
    </Drawer>
  </>;
}
export function ActiveCatalogFilters({ filters }: { filters: UseCatalogFiltersReturn }) {
  const items: { key: 'genre' | 'enrichmentStatus' | 'releaseCompleteness' | 'tracked'; label: string; reset: string | null }[] = [];
  if (filters.filters.genre) items.push({ key: 'genre', label: filters.filters.genre, reset: null });
  if (filters.filters.enrichmentStatus) items.push({ key: 'enrichmentStatus', label: `Metadata: ${STATUSES.find((s) => s.value === filters.filters.enrichmentStatus)?.label ?? filters.filters.enrichmentStatus}`, reset: null });
  if (filters.filters.releaseCompleteness !== 'all') items.push({ key: 'releaseCompleteness', label: `DAT: ${COMPLETENESS.find((s) => s.value === filters.filters.releaseCompleteness)?.label}`, reset: 'all' });
  if (!filters.lockedFilters.tracked && filters.filters.tracked !== 'all') items.push({ key: 'tracked', label: filters.filters.tracked === 'tracked' ? 'Tracked' : 'Untracked', reset: 'all' });
  return items.length ? <Group gap="xs">{items.map((item) => <Pill key={item.key} withRemoveButton onRemove={() => filters.setFilter(item.key, item.reset)}>{item.label}</Pill>)}<Button variant="subtle" color="gray" size="compact-xs" onClick={filters.clearFilters}>Clear filters</Button></Group> : null;
}
