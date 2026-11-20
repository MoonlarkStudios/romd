import {
  Alert,
  Badge,
  Button,
  NativeSelect,
  Stack,
  Table,
  Text,
  Title,
  UnstyledButton,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconChevronRight } from '@tabler/icons-react';
import { useState } from 'react';
import { useLocation, useSearchParams } from 'react-router';
import { InspectorLayout } from '../../components/Workspace/InspectorLayout';
import {
  useInspectorSelection,
  useInspectorSize,
} from '../../components/Workspace/useInspectorSelection';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useSetTitleTracking } from '../../hooks/api/useTitleActions';
import type { TitleReleaseView as TitleRelease } from '../../hooks/api/useTitleDetail';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { DiscSetsPanel, discSetRowId, useTitleDiscSets } from './DiscSetsPanel';
import { ReleaseInspector, type ReleasePreference } from './ReleaseInspector';
import classes from './ReleasesPanel.module.css';

const inspectorId = 'release-inspector';
const releaseFilterKeys = [
  'releaseRegion',
  'releaseOwnership',
  'releaseSource',
] as const;

export function releaseRegions(region: string | null | undefined): string[] {
  if (!region?.trim())
    return [
      'Unspecified',
    ];
  return region
    .split(/[,;/]/)
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => (/^(US|USA|United States)$/i.test(part) ? 'USA' : part));
}

function requiredReleaseFiles(release: TitleRelease) {
  return (release.files ?? []).filter((file) => file.status?.toLowerCase() !== 'nodump');
}

export function releaseOwnership(release: TitleRelease): 'Owned' | 'Partial' | 'Missing' {
  if (release.isComplete) return 'Owned';
  return requiredReleaseFiles(release).some((file) => file.isOwned) ? 'Partial' : 'Missing';
}

export function ReleasesPanel({
  releases,
  titleId,
  preference,
}: {
  releases: TitleRelease[];
  titleId: string;
  preference?: ReleasePreference;
}) {
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const { ref, wide } = useInspectorSize();
  const [reviewedSet, setReviewedSet] = useState<string | null>(null);
  const target = params.get('release');
  const linked = target
    ? (releases.find((release) => release.id === target) ??
      releases.find((release) => release.sources?.some((source) => source.datGameId === target)))
    : undefined;
  const region = params.get('releaseRegion') ?? '';
  const ownership = params.get('releaseOwnership') ?? '';
  const sourceId = params.get('releaseSource') ?? '';
  const updateFilters = (update: (next: URLSearchParams) => void) =>
    setParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        update(next);
        return next;
      },
      {
        replace: true,
        state: location.state,
      },
    );
  const updateFilter = (key: (typeof releaseFilterKeys)[number], value: string) =>
    updateFilters((next) => {
      if (value) next.set(key, value);
      else next.delete(key);
    });
  const clearFilters = () =>
    updateFilters((next) => {
      for (const key of releaseFilterKeys) next.delete(key);
    });
  const { data: references = [] } = useTitleSourceReferences(titleId);
  const { canManageTitles, hasRole } = usePermissions();
  const canReviewDiscSets = hasRole('Admin');
  const discSets = useTitleDiscSets(titleId, canReviewDiscSets);
  const tracking = useSetTitleTracking();
  const preferred = releases.find(
    (release) =>
      !!preference?.preferredCatalogReleaseId &&
      release.catalogReleaseId === preference.preferredCatalogReleaseId,
  );

  const setPin = (pinnedCatalogReleaseId: string | null) =>
    tracking.mutate(
      {
        titleId,
        tracked: true,
        pinnedCatalogReleaseId,
      },
      {
        onSuccess: () => {
          notifications.show({
            message: pinnedCatalogReleaseId
              ? 'Preferred release pinned'
              : 'Automatic release preference restored',
            color: 'green',
          });
        },
        onError: () =>
          notifications.show({
            message: 'Could not update release preference. Try again.',
            color: 'red',
          }),
      },
    );

  const regions = [
    ...new Set(releases.flatMap((release) => releaseRegions(release.region))),
  ];
  if (region && !regions.includes(region)) regions.push(region);
  regions.sort();

  const sourceOptions = new Map<string, string>();
  for (const reference of references)
    if (reference.datId) sourceOptions.set(reference.datId, reference.name ?? 'Catalog source');
  for (const release of releases) {
    if (!sourceOptions.has(release.datId)) sourceOptions.set(release.datId, 'Catalog source');
    for (const source of release.sources ?? []) sourceOptions.set(source.datId, source.datName);
  }
  if (sourceId && !sourceOptions.has(sourceId)) sourceOptions.set(sourceId, 'Unavailable source');

  const matches = (release: TitleRelease) =>
    (!region || releaseRegions(release.region).includes(region)) &&
    (!sourceId ||
      release.datId === sourceId ||
      release.sources?.some((source) => source.datId === sourceId)) &&
    (!ownership || releaseOwnership(release) === ownership);
  const filtered = releases.filter(matches);
  const linkedOutside = !!linked && !matches(linked);
  const visible = [
    ...filtered,
    ...(linkedOutside
      ? [
          linked,
        ]
      : []),
  ].sort(
    (a, b) =>
      Number(b === preferred) - Number(a === preferred) ||
      Number(!!b.isComplete) - Number(!!a.isComplete) ||
      a.name.localeCompare(b.name) ||
      a.id.localeCompare(b.id),
  );

  // Existing DAT entry links use a source ID. Keep it selected until the user changes releases.
  const selectionId = (release: TitleRelease) =>
    release === linked ? (target ?? release.id) : release.id;
  const selection = useInspectorSelection({
    param: 'release',
    ids: visible.map(selectionId),
    panelId: inspectorId,
  });
  const selected = selection.selectedId ? linked : undefined;

  return (
    <Stack
      gap="lg"
      ref={ref}
    >
      <InspectorLayout
        wide={wide}
        narrow="stack"
        inspector={
          selected && (
            <ReleaseInspector
              key={selected.id}
              id={inspectorId}
              release={selected}
              titleId={titleId}
              references={references}
              sourceName={sourceOptions.get(selected.datId)}
              preference={preference}
              canManageTitles={canManageTitles}
              savingPreference={tracking.isPending}
              onSetPin={setPin}
              discSets={canReviewDiscSets ? discSets.data : undefined}
              onReviewDiscSet={(id) => {
                setReviewedSet(id);
                document.getElementById(discSetRowId(id))?.querySelector('button')?.focus();
              }}
              onClose={selection.close}
            />
          )
        }
      >
        <Stack
          gap="sm"
          className={classes.directory}
        >
          <Title
            order={2}
            className={workspace.sectionHeading}
          >
            Releases
          </Title>
          <div className={classes.filters}>
            <NativeSelect
              label="Region"
              aria-label="Release region"
              size="sm"
              value={region}
              onChange={(event) => updateFilter('releaseRegion', event.currentTarget.value)}
              data={[
                {
                  value: '',
                  label: 'All regions',
                },
                ...regions.map((value) => ({
                  value,
                  label: value,
                })),
              ]}
            />
            <NativeSelect
              label="Ownership"
              size="sm"
              value={ownership}
              onChange={(event) => updateFilter('releaseOwnership', event.currentTarget.value)}
              data={[
                {
                  value: '',
                  label: 'Any ownership',
                },
                'Owned',
                'Missing',
                'Partial',
              ]}
            />
            <NativeSelect
              label="Source"
              size="sm"
              value={sourceId}
              onChange={(event) => updateFilter('releaseSource', event.currentTarget.value)}
              data={[
                {
                  value: '',
                  label: 'All sources',
                },
                ...Array.from(sourceOptions, ([value, label]) => ({
                  value,
                  label,
                })),
              ]}
            />
          </div>
          {target && (!linked || linkedOutside) && (
            <Alert
              title={
                linked ? 'Linked release is outside your filters' : 'This release is unavailable'
              }
            >
              {linkedOutside && (
                <Text size="sm">Shown alongside the releases that match your filters.</Text>
              )}
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={() => {
                  if (linked) selection.close();
                  else updateFilters((next) => next.delete('release'));
                }}
              >
                Dismiss linked release
              </Button>
            </Alert>
          )}
          <div className={classes.toolbar}>
            <Text
              size="xs"
              c="dimmed"
              role="status"
            >
              Showing {filtered.length} of {releases.length} releases
              {linkedOutside ? ' · plus linked release' : ''}
            </Text>
            {(region || ownership || sourceId) && (
              <Button
                {...workspaceActionProps}
                variant="subtle"
                size="compact-sm"
                onClick={clearFilters}
              >
                Clear filters
              </Button>
            )}
          </div>
          {visible.length > 0 ? (
            <div className={classes.tableSurface}>
              <Table
                aria-label="Releases"
                className={classes.table}
                verticalSpacing="sm"
                highlightOnHover
              >
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th scope="col">Release</Table.Th>
                    <Table.Th
                      scope="col"
                      className={classes.editionColumn}
                    >
                      Region
                    </Table.Th>
                    <Table.Th
                      scope="col"
                      className={classes.editionColumn}
                    >
                      Version
                    </Table.Th>
                    <Table.Th scope="col">Files</Table.Th>
                    <Table.Th scope="col">Ownership</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody onKeyDown={selection.onListKeyDown}>
                  {visible.map((release) => {
                    const id = selectionId(release);
                    const ownership = releaseOwnership(release);
                    const required = requiredReleaseFiles(release);
                    const ownedRequired = required.filter((file) => file.isOwned).length;
                    return (
                      <Table.Tr
                        key={release.id}
                        {...selection.rowProps(id)}
                      >
                        <Table.Td>
                          <UnstyledButton
                            className={classes.preview}
                            data-inspector-trigger
                            aria-expanded={selected === release}
                            aria-controls={selected === release ? inspectorId : undefined}
                            onClick={(event) => selection.select(id, event.currentTarget)}
                          >
                            <span className="romd-card-title">{release.name}</span>
                            <IconChevronRight
                              className={classes.chevron}
                              size={15}
                              aria-hidden
                            />
                          </UnstyledButton>
                          <Text
                            size="xs"
                            c="dimmed"
                            className={classes.compactEdition}
                          >
                            {release.region || 'Unspecified'}
                            {release.revisionLabel && ` · ${release.revisionLabel}`}
                          </Text>
                          {release === preferred && (
                            <Badge
                              size="xs"
                              variant="light"
                              mt={4}
                            >
                              Preferred ·{' '}
                              {release.catalogReleaseId === preference?.pinnedCatalogReleaseId
                                ? 'Pinned'
                                : 'Automatic'}
                            </Badge>
                          )}
                        </Table.Td>
                        <Table.Td className={classes.editionColumn}>
                          {release.region || 'Unspecified'}
                        </Table.Td>
                        <Table.Td className={classes.editionColumn}>
                          {release.revisionLabel || '—'}
                        </Table.Td>
                        <Table.Td>{release.files?.length ?? 0}</Table.Td>
                        <Table.Td>
                          <Text
                            size="sm"
                            c={ownership === 'Partial' ? 'yellow' : undefined}
                          >
                            {ownership}
                          </Text>
                          {required.length > 0 && (
                            <Text
                              size="xs"
                              c="dimmed"
                            >
                              {ownedRequired}/{required.length} required
                            </Text>
                          )}
                        </Table.Td>
                      </Table.Tr>
                    );
                  })}
                </Table.Tbody>
              </Table>
            </div>
          ) : (
            <div className={workspace.panel}>
              <Text
                size="sm"
                c="dimmed"
              >
                {releases.length === 0
                  ? 'No releases found for this title.'
                  : 'No releases match this view.'}
              </Text>
              {releases.length > 0 && (
                <Button
                  {...workspaceActionProps}
                  variant="subtle"
                  size="compact-sm"
                  mt="xs"
                  onClick={clearFilters}
                >
                  Show all releases
                </Button>
              )}
            </div>
          )}
        </Stack>
      </InspectorLayout>
      {canReviewDiscSets && (
        <DiscSetsPanel
          query={discSets}
          selectedId={reviewedSet}
          onSelect={setReviewedSet}
        />
      )}
    </Stack>
  );
}
