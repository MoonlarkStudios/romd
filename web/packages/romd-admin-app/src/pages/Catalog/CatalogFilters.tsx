import { Badge, Button, Checkbox, Group, Pill, Select } from '@mantine/core';
import { IconAdjustmentsHorizontal, IconChevronDown } from '@tabler/icons-react';
import { useId, useState } from 'react';
import {
  settledExplanation,
  titleStatusLabels,
} from '../../components/Title/enrichmentPresentation';
import { useManagedSystems } from '../../hooks/api/useManagedSystems';
import classes from './Catalog.module.css';
import {
  canonicalEnrichment,
  enrichmentFilters,
  lastAttemptLabels,
  lastAttemptStatuses,
  parseLastAttempt,
} from './catalogEnrichmentFilters';
import type {
  ReleaseCompletenessFilter,
  ReleaseReviewFilter,
  TrackedFilter,
  UseCatalogFiltersReturn,
} from './useCatalogFilters';

const ARTWORK = [
  {
    value: 'Poster',
    label: 'Missing dedicated poster',
  },
  {
    value: 'Backdrop',
    label: 'Missing backdrop',
  },
  {
    value: 'Logo',
    label: 'Missing logo',
  },
  {
    value: 'Hero',
    label: 'Missing banner',
  },
  {
    value: 'NeedsMatch',
    label: 'Artwork match needs review',
  },
];
const GENRES = [
  'Action',
  'Adventure',
  'Arcade',
  'Fighting',
  'Platform',
  'Puzzle',
  'Racing',
  'RPG',
  'Shooter',
  'Simulation',
  'Sports',
  'Strategy',
];
const LAST_ATTEMPTS = lastAttemptStatuses.map((value) => ({
  value,
  label: lastAttemptLabels[value],
}));
const RELEASE_REVIEW = [
  {
    value: 'all',
    label: 'Any tracked release state',
  },
  {
    value: 'missing',
    label: 'Without a complete release',
  },
  {
    value: 'upgrades',
    label: 'Preferred release missing',
  },
];
const COMPLETENESS = [
  {
    value: 'all',
    label: 'Any',
  },
  {
    value: 'complete',
    label: 'Files present for every DAT release',
  },
  {
    value: 'partial',
    label: 'Files present for some DAT releases',
  },
  {
    value: 'none',
    label: 'No files for DAT releases',
  },
];

interface Props {
  filters: UseCatalogFiltersReturn;
  lockedFilterLabels?: {
    systemKey?: string;
    tracked?: string;
  };
  showShortcuts?: boolean;
}
export function CatalogFilters({ filters, lockedFilterLabels }: Props) {
  const { data: systems, isLoading, isError } = useManagedSystems();
  const platforms = systems?.filter((system) => system.enabled);
  const count =
    Number(filters.filters.releaseReview !== 'all') +
    Number(!!filters.filters.artworkStatus) +
    Number(!!filters.filters.genre) +
    filters.filters.enrichment.length +
    Number(!!filters.filters.enrichmentStatus) +
    Number(filters.filters.releaseCompleteness !== 'all') +
    Number(!filters.lockedFilters.tracked && filters.filters.tracked !== 'all');
  const [opened, setOpened] = useState(count > 0);
  const panelId = useId();
  return (
    <>
      {filters.lockedFilters.systemKey ? (
        <Badge
          color="gray"
          variant="light"
        >
          {lockedFilterLabels?.systemKey ??
            platforms?.find((p) => p.key === filters.lockedFilters.systemKey)?.name ??
            filters.lockedFilters.systemKey}
        </Badge>
      ) : (
        <Select
          className={classes.systemFilter}
          label="System"
          placeholder="All systems"
          data={
            platforms?.map((p) => ({
              value: p.key,
              label: p.name,
            })) ?? []
          }
          value={filters.filters.systemKey ?? null}
          onChange={(v) => filters.setFilter('systemKey', v)}
          disabled={isLoading}
          error={isError ? 'Could not load systems' : undefined}
          nothingFoundMessage="No matching systems"
          clearable
          searchable
        />
      )}
      <Button
        variant="default"
        aria-expanded={opened}
        aria-controls={panelId}
        leftSection={<IconAdjustmentsHorizontal size={16} />}
        rightSection={
          <IconChevronDown
            size={14}
            style={{
              transform: opened ? 'rotate(180deg)' : undefined,
            }}
          />
        }
        onClick={() => setOpened((value) => !value)}
      >
        Filters{count ? ` (${count})` : ''}
      </Button>
      <section
        id={panelId}
        aria-label="Catalog filters"
        hidden={!opened}
        className={classes.filterPanel}
      >
        <div className={classes.filterFields}>
          <Select
            label="Genre"
            placeholder="Any genre"
            data={GENRES}
            value={filters.filters.genre ?? null}
            onChange={(v) => filters.setFilter('genre', v)}
            clearable
            searchable
          />
          <Select
            label="Artwork completion"
            placeholder="Any artwork status"
            data={ARTWORK}
            value={filters.filters.artworkStatus ?? null}
            onChange={(v) => filters.setFilter('artworkStatus', v)}
            clearable
          />
          <Select
            label="Last attempt"
            placeholder="Any last attempt"
            data={LAST_ATTEMPTS}
            value={filters.filters.enrichmentStatus ?? null}
            onChange={(v) => filters.setFilter('enrichmentStatus', parseLastAttempt(v) ?? null)}
            clearable
          />
          {!filters.lockedFilters.tracked && (
            <Select
              label="Tracking"
              placeholder="Any title"
              data={[
                {
                  value: 'tracked',
                  label: 'Tracked',
                },
                {
                  value: 'untracked',
                  label: 'Untracked',
                },
              ]}
              value={filters.filters.tracked === 'all' ? null : filters.filters.tracked}
              onChange={(v) => filters.setFilter('tracked', (v as TrackedFilter | null) ?? 'all')}
              clearable
            />
          )}
          <Select
            label="DAT release coverage"
            data={COMPLETENESS}
            value={filters.filters.releaseCompleteness}
            onChange={(v) =>
              filters.setFilter('releaseCompleteness', v as ReleaseCompletenessFilter)
            }
          />
          <Select
            label="Tracked release review"
            data={RELEASE_REVIEW}
            value={filters.filters.releaseReview}
            onChange={(value) =>
              filters.setFilter('releaseReview', (value as ReleaseReviewFilter | null) ?? 'all')
            }
          />
        </div>
        <Checkbox.Group
          label="Enrichment"
          description="Titles with all selected statuses"
          value={filters.filters.enrichment}
          onChange={(values) => filters.setFilter('enrichment', canonicalEnrichment(values))}
        >
          <Group
            gap="md"
            mt="xs"
            align="flex-start"
          >
            {enrichmentFilters.map((status) => (
              <Checkbox
                key={status}
                value={status}
                label={titleStatusLabels[status]}
                description={status === 'Settled' ? settledExplanation : undefined}
                disabled={status !== 'Settled' && filters.filters.enrichment.includes('Settled')}
              />
            ))}
          </Group>
        </Checkbox.Group>
      </section>
    </>
  );
}
export function ActiveCatalogFilters({ filters }: { filters: UseCatalogFiltersReturn }) {
  const { enrichment, enrichmentStatus } = filters.filters;
  const items: {
    key: string;
    label: string;
    remove: () => void;
  }[] = [];
  if (filters.filters.releaseReview !== 'all')
    items.push({
      key: 'releaseReview',
      label:
        RELEASE_REVIEW.find((item) => item.value === filters.filters.releaseReview)?.label ??
        'Release review',
      remove: () => filters.setFilter('releaseReview', 'all'),
    });
  if (filters.filters.artworkStatus)
    items.push({
      key: 'artworkStatus',
      label: ARTWORK.find((x) => x.value === filters.filters.artworkStatus)?.label ?? 'Artwork',
      remove: () => filters.setFilter('artworkStatus', null),
    });
  if (filters.filters.genre)
    items.push({
      key: 'genre',
      label: filters.filters.genre,
      remove: () => filters.setFilter('genre', null),
    });
  for (const status of enrichment)
    items.push({
      key: `enrichment-${status}`,
      label: `Enrichment: ${titleStatusLabels[status]}`,
      remove: () =>
        filters.setFilter(
          'enrichment',
          enrichment.filter((value) => value !== status),
        ),
    });
  if (enrichmentStatus)
    items.push({
      key: 'enrichmentStatus',
      label: `Last attempt: ${lastAttemptLabels[enrichmentStatus]}`,
      remove: () => filters.setFilter('enrichmentStatus', null),
    });
  if (filters.filters.releaseCompleteness !== 'all')
    items.push({
      key: 'releaseCompleteness',
      label: `DAT: ${COMPLETENESS.find((s) => s.value === filters.filters.releaseCompleteness)?.label}`,
      remove: () => filters.setFilter('releaseCompleteness', 'all'),
    });
  if (!filters.lockedFilters.tracked && filters.filters.tracked !== 'all')
    items.push({
      key: 'tracked',
      label: filters.filters.tracked === 'tracked' ? 'Tracked' : 'Untracked',
      remove: () => filters.setFilter('tracked', 'all'),
    });
  return items.length ? (
    <Group gap="xs">
      {items.map((item) => (
        <Pill
          key={item.key}
          withRemoveButton
          onRemove={item.remove}
        >
          {item.label}
        </Pill>
      ))}
      <Button
        variant="subtle"
        color="gray"
        size="compact-xs"
        onClick={filters.clearFilters}
      >
        Clear filters
      </Button>
    </Group>
  ) : null;
}
