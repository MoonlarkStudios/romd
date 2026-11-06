import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Drawer,
  Group,
  NativeSelect,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconChevronDown, IconChevronRight, IconPin, IconPinnedFilled } from '@tabler/icons-react';
import { DataTable } from 'mantine-datatable';
import { useEffect, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useSetTitleTracking } from '../../hooks/api/useTitleActions';
import type {
  TitleDetailView as TitleDetail,
  TitleReleaseView as TitleRelease,
} from '../../hooks/api/useTitleDetail';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { SourceStatusBadge } from '../Systems/SourceStatusBadge';
import { FileRequirements } from './FileRequirements';
import { ReleaseDownloadButton } from './ReleaseDownloadButton';
import classes from './ReleasesPanel.module.css';
import { SourceReferencesPanel } from './SourceReferencesPanel';

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

export function requiredReleaseFiles(release: TitleRelease) {
  return (release.files ?? []).filter((file) => file.status?.toLowerCase() !== 'nodump');
}

export function releaseOwnership(release: TitleRelease): 'Owned' | 'Partial' | 'Missing' {
  if (release.isComplete) return 'Owned';
  return requiredReleaseFiles(release).some((file) => file.isOwned) ? 'Partial' : 'Missing';
}

type Preference = Pick<
  TitleDetail,
  | 'isTracked'
  | 'pinnedCatalogReleaseId'
  | 'preferredCatalogReleaseId'
  | 'preferredReleaseName'
  | 'releaseRegionPriority'
>;

export function ReleasesPanel({
  releases,
  titleId,
  preference,
}: {
  releases: TitleRelease[];
  titleId: string;
  preference?: Preference;
}) {
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const target = params.get('release');
  const selected = releases.find(
    (release) =>
      release.id === target || release.sources?.some((source) => source.datGameId === target),
  );
  const [detailId, setDetailId] = useState<string | null>(null);
  const [expandedIds, setExpandedIds] = useState<string[]>([]);
  const [sourcesOpened, setSourcesOpened] = useState(false);
  const selectedId = selected?.id;
  useEffect(() => {
    if (selectedId)
      setExpandedIds((ids) =>
        ids.includes(selectedId)
          ? ids
          : [
              ...ids,
              selectedId,
            ],
      );
  }, [
    selectedId,
  ]);
  const region = params.get('releaseRegion') ?? '';
  const ownership = params.get('releaseOwnership') ?? '';
  const sourceId = params.get('releaseSource') ?? '';
  const updateFilter = (key: string, value: string) =>
    setParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        if (value) next.set(key, value);
        else next.delete(key);
        return next;
      },
      {
        replace: true,
        state: location.state,
      },
    );
  const clearFilters = () =>
    setParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        for (const key of [
          'releaseRegion',
          'releaseOwnership',
          'releaseSource',
        ])
          next.delete(key);
        return next;
      },
      {
        replace: true,
        state: location.state,
      },
    );
  const { data: references = [] } = useTitleSourceReferences(titleId);
  const { canManageTitles } = usePermissions();
  const tracking = useSetTitleTracking();
  const preferred = releases.find(
    (release) =>
      !!preference?.preferredCatalogReleaseId &&
      release.catalogReleaseId === preference.preferredCatalogReleaseId,
  );
  const isPreferred = (release: TitleRelease) => release === preferred;
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
            color: 'teal',
          });
        },
        onError: () =>
          notifications.show({
            message: 'Could not update release preference. Try again.',
            color: 'red',
          }),
      },
    );
  const regions = Array.from(
    new Set([
      ...releases.flatMap((release) => releaseRegions(release.region)),
      ...(region
        ? [
            region,
          ]
        : []),
    ]),
  ).sort();
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
  const linkedOutside = !!selected && !matches(selected);
  const visible = [
    ...filtered,
    ...(linkedOutside
      ? [
          selected,
        ]
      : []),
  ].sort(
    (a, b) =>
      Number(isPreferred(b)) - Number(isPreferred(a)) ||
      Number(!!b.isComplete) - Number(!!a.isComplete) ||
      a.name.localeCompare(b.name) ||
      a.id.localeCompare(b.id),
  );
  const detail = releases.find((release) => release.id === detailId);
  const sourcesFor = (release: TitleRelease) =>
    release.sources?.length
      ? release.sources
      : [
          {
            datId: release.datId,
            datGameId: release.id,
            datName: sourceOptions.get(release.datId) ?? 'Catalog source',
            gameName: release.name,
          },
        ];
  const toggleFiles = (id: string) =>
    setExpandedIds((ids) =>
      ids.includes(id)
        ? ids.filter((value) => value !== id)
        : [
            ...ids,
            id,
          ],
    );
  const preferenceExplanation = preference?.pinnedCatalogReleaseId
    ? 'Pinned for this title. Overrides automatic preference.'
    : `Automatically selected by dump quality, region priority (${Array.from(new Set(preference?.releaseRegionPriority?.map((value) => value.toUpperCase()))).join(' → ')}), then highest numeric revision.`;
  return (
    <Stack gap="md">
      {target && (
        <Alert
          color="blue"
          title={
            selected
              ? linkedOutside
                ? 'Linked release is outside your filters'
                : 'Linked release highlighted'
              : 'The linked release is no longer available.'
          }
        >
          {linkedOutside && <Text size="sm">Shown below alongside matching releases.</Text>}
          <Button
            variant="subtle"
            size="xs"
            onClick={() => updateFilter('release', '')}
          >
            Dismiss linked release
          </Button>
        </Alert>
      )}
      <Group
        align="flex-end"
        gap="sm"
      >
        <NativeSelect
          label="Region"
          aria-label="Release region"
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
          style={{
            maxWidth: '100%',
            minWidth: 0,
          }}
        />
        {(region || ownership || sourceId) && (
          <Button
            {...workspaceActionProps}
            variant="subtle"
            size="xs"
            onClick={clearFilters}
          >
            Clear filters
          </Button>
        )}
      </Group>
      <div className={classes.toolbar}>
        <Text
          size="xs"
          c="dimmed"
          aria-live="polite"
        >
          Showing {filtered.length} of {releases.length} releases
          {linkedOutside ? ' · plus linked release' : ''}
        </Text>
        <Button
          {...workspaceActionProps}
          size="xs"
          variant="subtle"
          onClick={() => setSourcesOpened(true)}
        >
          Sources ({references.length})
        </Button>
      </div>
      {visible.length > 0 ? (
        <DataTable<TitleRelease>
          records={visible}
          minHeight={0}
          verticalSpacing="sm"
          highlightOnHover
          className={classes.table}
          styles={{
            table: {
              minWidth: 1000,
            },
          }}
          customRowAttributes={(release) => ({
            'data-linked': selected?.id === release.id || undefined,
          })}
          rowExpansion={{
            trigger: 'never',
            allowMultiple: true,
            expanded: {
              recordIds: expandedIds,
            },
            content: ({ record }) => (
              <div className={classes.files}>
                <FileRequirements files={record.files ?? []} />
              </div>
            ),
          }}
          columns={[
            {
              accessor: 'name',
              title: 'Release',
              width: 280,
              render: (release) => (
                <Anchor
                  component="button"
                  type="button"
                  ta="left"
                  size="sm"
                  fw={600}
                  aria-expanded={expandedIds.includes(release.id)}
                  onClick={() => toggleFiles(release.id)}
                >
                  <Group
                    gap={6}
                    wrap="nowrap"
                  >
                    {expandedIds.includes(release.id) ? (
                      <IconChevronDown
                        size={16}
                        aria-hidden="true"
                      />
                    ) : (
                      <IconChevronRight
                        size={16}
                        aria-hidden="true"
                      />
                    )}
                    <span>{release.name}</span>
                  </Group>
                </Anchor>
              ),
            },
            {
              accessor: 'region',
              title: 'Region',
              render: (release) => <Text size="sm">{release.region || 'Unspecified'}</Text>,
            },
            {
              accessor: 'revisionLabel',
              title: 'Revision',
              render: (release) => (
                <Text
                  size="sm"
                  c="dimmed"
                >
                  {release.revisionLabel || '—'}
                </Text>
              ),
            },
            {
              accessor: 'files',
              title: 'Files',
              render: (release) => {
                const status = releaseOwnership(release);
                const required = requiredReleaseFiles(release);
                return (
                  <Stack gap={4}>
                    <Text
                      size="sm"
                      c={status === 'Partial' ? 'yellow' : undefined}
                    >
                      {status === 'Owned' ? 'Complete' : status}
                    </Text>
                    <Text
                      size="xs"
                      c="dimmed"
                    >
                      {required.filter((file) => file.isOwned).length}/{required.length} required
                    </Text>
                  </Stack>
                );
              },
            },
            {
              accessor: 'preference',
              title: 'Preference',
              render: (release) => {
                const pinned =
                  !!release.catalogReleaseId &&
                  release.catalogReleaseId === preference?.pinnedCatalogReleaseId;
                const hint = pinned
                  ? 'Unpin release and restore automatic preference.'
                  : !release.catalogReleaseId
                    ? 'This release cannot be pinned.'
                    : preference?.isTracked
                      ? 'Set as preferred. Overrides automatic preference.'
                      : 'Set as preferred. Tracks this title and pins this release.';
                return (
                  <Group
                    gap="xs"
                    wrap="nowrap"
                  >
                    {canManageTitles && (
                      <Tooltip
                        label={hint}
                        multiline
                        w={280}
                      >
                        <ActionIcon
                          {...workspaceActionProps}
                          variant={pinned ? 'light' : 'subtle'}
                          aria-label={`${pinned ? 'Unpin release' : 'Set as preferred'}: ${release.name}`}
                          aria-pressed={pinned}
                          disabled={!release.catalogReleaseId || tracking.isPending}
                          onClick={() => {
                            if (release.catalogReleaseId)
                              setPin(pinned ? null : release.catalogReleaseId);
                          }}
                        >
                          {pinned ? <IconPinnedFilled size={16} /> : <IconPin size={16} />}
                        </ActionIcon>
                      </Tooltip>
                    )}
                    {isPreferred(release) ? (
                      <Tooltip
                        label={preferenceExplanation}
                        multiline
                        w={300}
                      >
                        <Text
                          size="xs"
                          tabIndex={0}
                        >
                          Preferred · {preference?.pinnedCatalogReleaseId ? 'Pinned' : 'Automatic'}
                        </Text>
                      </Tooltip>
                    ) : (
                      <Text
                        size="xs"
                        c="dimmed"
                      >
                        —
                      </Text>
                    )}
                  </Group>
                );
              },
            },
            {
              accessor: 'sources',
              title: 'Sources',
              render: (release) => {
                const sources = Array.from(
                  new Set(sourcesFor(release).map((source) => source.datName)),
                );
                return (
                  <Anchor
                    component="button"
                    type="button"
                    className={classes.sourceLink}
                    ta="left"
                    size="xs"
                    aria-label={`Sources for ${release.name}`}
                    onClick={() => setDetailId(release.id)}
                  >
                    {sources[0]}
                    {sources.length > 1 ? ` +${sources.length - 1}` : ''}
                  </Anchor>
                );
              },
            },
            {
              accessor: 'download',
              title: 'Download',
              render: (release) => (
                <ReleaseDownloadButton
                  files={release.files ?? []}
                  formats={release.archiveFormats ?? []}
                  name={release.name}
                  releaseId={release.catalogReleaseId}
                />
              ),
            },
          ]}
        />
      ) : (
        <Stack
          gap="xs"
          align="flex-start"
          py="md"
        >
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
              size="xs"
              onClick={clearFilters}
            >
              Show all releases
            </Button>
          )}
        </Stack>
      )}
      <Drawer
        closeButtonProps={{
          'aria-label': 'Close release details',
        }}
        opened={!!detail}
        onClose={() => setDetailId(null)}
        position="right"
        size="lg"
        title={detail?.name ?? 'Release details'}
      >
        {detail && (
          <Stack gap="md">
            <Group gap="xs">
              <Text
                size="sm"
                c="dimmed"
              >
                {[
                  detail.region,
                  detail.revisionLabel,
                  detail.language,
                  detail.year,
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </Text>
              {isPreferred(detail) && (
                <Badge
                  color="teal"
                  variant="light"
                >
                  Preferred · {preference?.pinnedCatalogReleaseId ? 'Pinned' : 'Automatic'}
                </Badge>
              )}
            </Group>
            <Text
              size="sm"
              fw={600}
            >
              Catalog sources ({sourcesFor(detail).length})
            </Text>
            <Stack gap="md">
              {sourcesFor(detail).map((source) => {
                const reference = references.find((ref) => ref.datId === source.datId);
                return (
                  <Stack
                    key={source.datGameId}
                    gap={4}
                  >
                    <Group gap="xs">
                      {reference?.systemKey ? (
                        <Anchor
                          component={Link}
                          size="sm"
                          to={`/systems/${reference.systemKey}?dat=${source.datId}&sourceTitle=${titleId}`}
                        >
                          {source.datName}
                        </Anchor>
                      ) : (
                        <Text size="sm">{source.datName}</Text>
                      )}
                      {reference && <SourceStatusBadge status={reference.status} />}
                    </Group>
                    <Text
                      size="sm"
                      c="dimmed"
                    >
                      {source.gameName}
                    </Text>
                  </Stack>
                );
              })}
            </Stack>
          </Stack>
        )}
      </Drawer>
      <Drawer
        closeButtonProps={{
          'aria-label': 'Close catalog sources',
        }}
        opened={sourcesOpened}
        onClose={() => setSourcesOpened(false)}
        position="right"
        size="lg"
        title="Catalog sources"
      >
        <SourceReferencesPanel titleId={titleId} />
      </Drawer>
    </Stack>
  );
}
