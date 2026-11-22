import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Menu,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { JobItem } from '@romd/admin-api-client';
import { IconChevronLeft, IconChevronRight, IconDownload, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { useJobItems } from '../../hooks/api/useJobItems';
import { usePermissions } from '../../hooks/usePermissions';
import { authenticatedDownload } from '../../utils/download';
import { formatBytes } from '../../utils/format';
import { importResultTarget, receiptFilters, uploadImportUrl } from './importReceiptLinks';

const OUTCOMES: Record<
  string,
  {
    label: string;
    color: string;
  }
> = {
  ingested: {
    label: 'Stored',
    color: 'teal',
  },
  deduplicated: {
    label: 'Already stored',
    color: 'gray',
  },
  rejected: {
    label: 'Not stored',
    color: 'orange',
  },
  failed: {
    label: 'Failed',
    color: 'red',
  },
  dat_routed: {
    label: 'Catalog imported',
    color: 'teal',
  },
  dat_unrouted: {
    label: 'No system at import',
    color: 'orange',
  },
};

function NextStep({
  item,
  destination,
  canRoute,
  retryUrl,
}: {
  item: JobItem;
  destination: (path: string) => string;
  canRoute: boolean;
  retryUrl: string | null;
}) {
  if (item.currentStatus === 'Unavailable')
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        File or catalog no longer available
      </Text>
    );
  if (item.kind === 'dat') {
    const systemKey = item.currentSystemKey ?? item.systemKey;
    if (
      item.datFileId &&
      (item.currentStatus === 'Unrouted' ||
        (!item.currentStatus && item.outcome === 'dat_unrouted'))
    ) {
      return canRoute ? (
        <Anchor
          component={Link}
          size="sm"
          to={destination(`/roms?tab=attention&dat=${item.datFileId}`)}
        >
          Route this catalog
        </Anchor>
      ) : (
        <Text size="sm">Ask a Manager to route this catalog.</Text>
      );
    }
    if (systemKey && item.datFileId) {
      return (
        <Anchor
          component={Link}
          size="sm"
          to={`/systems/${systemKey}?tab=sources&dat=${item.datFileId}`}
        >
          {item.currentSystemKey && item.currentSystemKey !== item.systemKey
            ? 'Open current system'
            : (item.platformName ?? 'Open system')}
        </Anchor>
      );
    }
    return item.outcome === 'failed' ? (
      <Text size="sm">Check the catalog before importing it again.</Text>
    ) : null;
  }
  if (
    item.romFileId &&
    (item.currentStatus === 'Unidentified' || item.currentStatus === 'Unrouted')
  ) {
    return (
      <Stack gap={4}>
        <Anchor
          component={Link}
          size="sm"
          to={destination(`/roms/${item.romFileId}?tab=all`)}
        >
          Review stored file
        </Anchor>
        <Text
          size="xs"
          c="dimmed"
        >
          {item.currentStatus === 'Unidentified'
            ? 'Import a matching DAT to identify it.'
            : 'Check the catalog routing.'}
        </Text>
      </Stack>
    );
  }
  if (item.reason === 'DiscDescriptorKept') {
    return (
      <Anchor
        component={Link}
        size="sm"
        to="/disc-layouts"
      >
        Review disc descriptors
      </Anchor>
    );
  }
  if (item.outcome === 'rejected' || item.outcome === 'failed') {
    const guidance =
      item.reason === 'OutsideTrackedSelection'
        ? 'Review the tracked selection.'
        : item.reason === 'NoCatalogMatch'
          ? 'Check that the matching DAT is installed.'
          : item.reason === 'UnsupportedArchive'
            ? 'Use ZIP or 7z.'
            : item.outcome === 'failed'
              ? 'Review the error before trying again.'
              : 'Review the import settings.';
    return (
      <Stack gap={4}>
        <Text size="sm">{guidance}</Text>
        {retryUrl && (
          <Anchor
            component={Link}
            size="sm"
            to={retryUrl}
          >
            {retryUrl === uploadImportUrl ? 'Back to Upload' : 'Open server import folder'}
          </Anchor>
        )}
      </Stack>
    );
  }
  const systemKey =
    item.matchedTitles.length === 1 ? (item.currentSystemKey ?? item.systemKey) : undefined;
  return (
    <Stack gap={4}>
      {item.matchedTitles.map((title) => (
        <Anchor
          key={title.id}
          component={Link}
          size="sm"
          to={
            canRoute
              ? destination(
                  `/catalog?${new URLSearchParams({
                    view: 'review',
                    title: title.id,
                    ...(systemKey
                      ? {
                          systemKey,
                        }
                      : {}),
                  })}`,
                )
              : `/titles/${title.id}`
          }
        >
          {title.name}
        </Anchor>
      ))}
      <Text
        size="xs"
        c="dimmed"
      >
        {[
          item.platformName,
          canRoute && item.matchedTitles.length > 0 && 'Review in Catalog',
        ]
          .filter(Boolean)
          .join(' · ')}
      </Text>
    </Stack>
  );
}

export function ProvenanceLedger({
  jobId,
  addressable = false,
  retryUrl = uploadImportUrl,
}: {
  jobId: string;
  addressable?: boolean;
  retryUrl?: string | null;
}) {
  const location = useLocation();
  const { canManageTitles } = usePermissions();
  const navigate = useNavigate();
  const params = new URLSearchParams(location.search);
  const [filter, setFilter] = useState(() => {
    const outcome = addressable ? params.get('fileOutcome') : null;
    return outcome && Object.hasOwn(OUTCOMES, outcome) ? outcome : 'all';
  });
  const [search, setSearch] = useState(() =>
    addressable ? (params.get('fileQuery')?.slice(0, 200) ?? '') : '',
  );
  const [term] = useDebouncedValue(search, 250);
  const [paging, setPaging] = useState<{
    key: string;
    cursors: (string | undefined)[];
  }>({
    key: '',
    cursors: [
      undefined,
    ],
  });
  const key = `${jobId}:${filter}:${term}`;
  const cursors =
    paging.key === key
      ? paging.cursors
      : [
          undefined,
        ];
  const query = useJobItems(jobId, filter === 'all' ? undefined : filter, {
    cursor: cursors.at(-1),
    search: term || undefined,
  });
  const updateFilters = (nextFilter: string, nextSearch: string) => {
    setFilter(nextFilter);
    setSearch(nextSearch);
    if (addressable) {
      const next = receiptFilters(nextFilter, nextSearch);
      void navigate(
        {
          search: next.toString(),
          hash: location.hash,
        },
        {
          replace: true,
          preventScrollReset: true,
          state: location.state,
        },
      );
    }
    setPaging({
      key: '',
      cursors: [
        undefined,
      ],
    });
  };
  const exportItems = async (format: 'csv' | 'json') => {
    try {
      await authenticatedDownload(
        `/api/jobs/${jobId}/item-export?format=${format}`,
        `import.${format}`,
      );
    } catch {
      notifications.show({
        color: 'red',
        message: 'Export failed.',
      });
    }
  };
  if (!query.isError && query.data?.items.length === 0 && filter === 'all' && !search && !term) {
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No file outcomes were recorded.
      </Text>
    );
  }

  const destination = (path: string) => importResultTarget(path, jobId, filter, search);
  return (
    <Stack gap="md">
      <Group gap="sm">
        <TextInput
          aria-label="Search imported files"
          placeholder="Search file paths"
          leftSection={<IconSearch size={16} />}
          value={search}
          maxLength={200}
          onChange={(event) => updateFilters(filter, event.currentTarget.value)}
          style={{
            flex: '1 1 220px',
          }}
        />
        <Select
          aria-label="Filter outcomes"
          value={filter}
          onChange={(value) => updateFilters(value ?? 'all', search)}
          data={[
            {
              value: 'all',
              label: 'All outcomes',
            },
            ...Object.entries(OUTCOMES).map(([value, meta]) => ({
              value,
              label: meta.label,
            })),
          ]}
          w={220}
        />
        <Menu>
          <Menu.Target>
            <ActionIcon
              variant="subtle"
              color="gray"
              aria-label="Export details"
            >
              <IconDownload size={18} />
            </ActionIcon>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item onClick={() => void exportItems('csv')}>Download CSV</Menu.Item>
            <Menu.Item onClick={() => void exportItems('json')}>Download JSON</Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>
      {query.isError ? (
        <Alert
          color="red"
          title="Could not load file results"
        >
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry results
          </Button>
        </Alert>
      ) : query.isPending ? (
        <Group
          justify="center"
          py="lg"
        >
          <Loader size="sm" />
        </Group>
      ) : !query.data?.items.length ? (
        <Text
          size="sm"
          c="dimmed"
          py="lg"
        >
          No files match these filters.
        </Text>
      ) : (
        <>
          <Table.ScrollContainer minWidth={650}>
            <Table
              layout="fixed"
              verticalSpacing="sm"
              highlightOnHover
              aria-label="Import results"
            >
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w="48%">File</Table.Th>
                  <Table.Th w="20%">Outcome</Table.Th>
                  <Table.Th w="32%">Matches or next step</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {query.data.items.map((item) => {
                  const meta = OUTCOMES[item.outcome] ?? {
                    label: item.outcome,
                    color: 'gray',
                  };
                  return (
                    <Table.Tr
                      key={item.id}
                      style={{
                        verticalAlign: 'top',
                        overflowWrap: 'anywhere',
                      }}
                    >
                      <Table.Td>
                        <Stack gap={4}>
                          {item.romFileId && item.currentStatus !== 'Unavailable' ? (
                            <Anchor
                              component={Link}
                              size="sm"
                              to={destination(`/roms/${item.romFileId}?tab=all`)}
                            >
                              {item.fileName}
                            </Anchor>
                          ) : (
                            <Text size="sm">{item.fileName}</Text>
                          )}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {formatBytes(item.sizeBytes)}
                          </Text>
                          {item.error && (
                            <Text
                              size="xs"
                              c="red"
                            >
                              {item.error}
                            </Text>
                          )}
                        </Stack>
                      </Table.Td>
                      <Table.Td>
                        <Badge
                          size="sm"
                          variant="light"
                          color={meta.color}
                          styles={{
                            root: {
                              height: 'auto',
                              minHeight: 'var(--badge-height)',
                              textAlign: 'left',
                            },
                            label: {
                              whiteSpace: 'normal',
                            },
                          }}
                        >
                          {item.reason === 'OutsideTrackedSelection'
                            ? 'Skipped by policy'
                            : item.reason === 'KeptWithoutMatch'
                              ? 'Kept without a match'
                              : meta.label}
                        </Badge>
                      </Table.Td>
                      <Table.Td>
                        <NextStep
                          item={item}
                          destination={destination}
                          canRoute={canManageTitles}
                          retryUrl={retryUrl}
                        />
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <Group justify="space-between">
            <Text
              size="xs"
              c="dimmed"
            >
              Page {cursors.length} · {query.data.items.length}{' '}
              {query.data.items.length === 1 ? 'file' : 'files'}
            </Text>
            <Group gap="xs">
              <Button
                variant="default"
                size="xs"
                leftSection={<IconChevronLeft size={14} />}
                disabled={cursors.length === 1}
                onClick={() =>
                  setPaging({
                    key,
                    cursors: cursors.slice(0, -1),
                  })
                }
              >
                Previous
              </Button>
              <Button
                variant="default"
                size="xs"
                rightSection={<IconChevronRight size={14} />}
                disabled={!query.data.hasNextPage || !query.data.nextCursor}
                onClick={() =>
                  setPaging({
                    key,
                    cursors: [
                      ...cursors,
                      query.data?.nextCursor ?? undefined,
                    ],
                  })
                }
              >
                Next
              </Button>
            </Group>
          </Group>
        </>
      )}
    </Stack>
  );
}
