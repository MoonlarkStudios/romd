import {
  Badge,
  Button,
  Center,
  Group,
  Loader,
  Menu,
  Paper,
  SegmentedControl,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type {
  BiosFilter,
  Dat,
  DatGame,
  PageOfDatGame,
  SourceLifecycleStatus,
} from '@romd/admin-api-client';
import { listGamesByDat } from '@romd/admin-api-client';
import { IconChevronDown, IconDatabase, IconDownload, IconUpload } from '@tabler/icons-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useCallback, useMemo, useState } from 'react';
import { datKeys, downloadDatSource } from '../../hooks/api/useDats';
import { usePermissions } from '../../hooks/usePermissions';
import { formatBytes } from '../../utils/format';
import { ConfirmSourceStatusModal } from './ConfirmSourceStatusModal';
import { DatGameRow } from './DatGameRow';
import { SourceEntryExplorer } from './SourceEntryExplorer';
import { SourceStatusBadge } from './SourceStatusBadge';

/** Columns rendered by GamesTable — the detail row spans all of them. */
const GAME_COLUMN_COUNT = 4;

const datGameKeys = {
  byDat: (datId: string, bios: BiosFilter) =>
    [
      ...datKeys.detail(datId),
      'games',
      bios,
    ] as const,
};

const ALL_STATUSES: readonly SourceLifecycleStatus[] = [
  'Active',
  'Discontinued',
  'Disabled',
];

/** Menu labels phrase each status as the action that reaches it. */
const STATUS_ACTION_LABELS: Record<SourceLifecycleStatus, string> = {
  Active: 'Re-enable Source',
  Discontinued: 'Mark Discontinued',
  Disabled: 'Disable Source',
};

function useGamesByDat(datId: string, bios: BiosFilter, enabled: boolean, limit = 50) {
  return useInfiniteQuery({
    queryKey: datGameKeys.byDat(datId, bios),
    enabled,
    queryFn: async ({ pageParam, signal }) => {
      const response = await listGamesByDat({
        signal,
        path: {
          datId,
        },
        query: {
          limit,
          cursor: pageParam,
          bios,
        },
      });

      if (response.error) {
        throw new Error('Failed to fetch games');
      }

      return response.data as PageOfDatGame;
    },
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor : undefined),
    initialPageParam: undefined as string | undefined,
  });
}

function formatDate(dateString?: string | null): string {
  if (!dateString) return '—';
  return new Date(dateString).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

interface GamesTableProps {
  games: DatGame[];
  bios: BiosFilter;
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  onLoadMore: () => void;
}

function GamesTable({ games, bios, hasNextPage, isFetchingNextPage, onLoadMore }: GamesTableProps) {
  if (games.length === 0) {
    return (
      <Center py={40}>
        <Text
          c="dimmed"
          size="sm"
        >
          {bios === 'Only' ? 'No BIOS entries in this catalog.' : 'No games in this catalog.'}
        </Text>
      </Center>
    );
  }

  return (
    <>
      <Paper
        radius="md"
        withBorder
        style={{
          overflow: 'hidden',
        }}
      >
        <Table.ScrollContainer minWidth={500}>
          <Table
            verticalSpacing="sm"
            highlightOnHover
          >
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th w={90}>Year</Table.Th>
                <Table.Th>Publisher</Table.Th>
                <Table.Th
                  ta="right"
                  w={80}
                >
                  Files
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {games.map((game) => (
                <DatGameRow
                  key={game.id}
                  game={game}
                  columnCount={GAME_COLUMN_COUNT}
                />
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Paper>

      {hasNextPage && (
        <Center mt="md">
          <Button
            variant="light"
            color="gray"
            size="sm"
            onClick={onLoadMore}
            loading={isFetchingNextPage}
          >
            Load More Games
          </Button>
        </Center>
      )}
    </>
  );
}

export interface DatDetailPaneProps {
  dat: Dat;
  /** Opens the Sources drop zone — a re-upload with a matching header replaces this DAT */
  onReplace: () => void;
  allowReplacement?: boolean;
}

/** The detail half of Sources: one DAT's identity, counts, and game entries. */
export function DatDetailPane({ dat, onReplace, allowReplacement = true }: DatDetailPaneProps) {
  const { canManageSources } = usePermissions();
  const [bios, setBios] = useState<BiosFilter>('Exclude');
  const [view, setView] = useState('entries');
  const [pendingStatus, setPendingStatus] = useState<SourceLifecycleStatus | 'Delete' | null>(null);
  const { data, isLoading, isError, refetch, hasNextPage, fetchNextPage, isFetchingNextPage } =
    useGamesByDat(dat.id, bios, view === 'files');

  const otherStatuses = ALL_STATUSES.filter((status) => status !== dat.sourceStatus);

  const games = useMemo(
    () => data?.pages.flatMap((page) => page.items) ?? [],
    [
      data,
    ],
  );
  const handleDownload = useCallback(() => {
    downloadDatSource(dat).catch(() => {
      notifications.show({
        title: 'Download failed',
        message: `Could not download ${dat.name}.`,
        color: 'red',
      });
    });
  }, [
    dat,
  ]);

  return (
    <Stack gap="md">
      <Group
        justify="space-between"
        align="flex-start"
      >
        <Stack
          gap={4}
          style={{
            minWidth: 0,
          }}
        >
          <Group
            gap="sm"
            wrap="nowrap"
          >
            <IconDatabase
              size={20}
              color="var(--mantine-color-dimmed)"
              style={{
                flexShrink: 0,
              }}
            />
            <Title
              order={4}
              style={{
                minWidth: 0,
              }}
              lineClamp={1}
            >
              {dat.name}
            </Title>
          </Group>
          <Group gap="sm">
            <Badge
              variant="light"
              color="gray"
              radius="sm"
            >
              {dat.type}
            </Badge>
            <SourceStatusBadge status={dat.sourceStatus} />
            {dat.version && (
              <Badge
                variant="light"
                color="blue"
                radius="sm"
                style={{
                  fontFamily: 'var(--mantine-font-family-monospace)',
                }}
              >
                v{dat.version}
              </Badge>
            )}
          </Group>
          <Text
            size="sm"
            c="dimmed"
          >
            <Text
              component="span"
              fw={600}
            >
              {Number(dat.gameCount ?? 0).toLocaleString()}
            </Text>{' '}
            games
            {' · '}
            <Text
              component="span"
              fw={600}
            >
              {Number(dat.romCount ?? 0).toLocaleString()}
            </Text>{' '}
            ROMs
            {dat.sourceFile && (
              <>
                {' · '}
                <Text
                  component="span"
                  fw={600}
                >
                  {formatBytes(dat.sourceFile.sizeOnDiskBytes)}
                </Text>{' '}
                on disk
              </>
            )}
            {' · '}imported {formatDate(dat.importedAt)}
          </Text>
        </Stack>

        <Group
          gap="xs"
          style={{
            flexShrink: 0,
          }}
        >
          <Button
            variant="light"
            color="gray"
            size="sm"
            leftSection={<IconDownload size={16} />}
            onClick={handleDownload}
          >
            Download Source
          </Button>
          {allowReplacement && (
            <Button
              variant="light"
              color="gray"
              size="sm"
              leftSection={<IconUpload size={16} />}
              onClick={onReplace}
            >
              Upload new version
            </Button>
          )}
          {canManageSources && (
            <Menu
              shadow="md"
              width={200}
              position="bottom-end"
            >
              <Menu.Target>
                <Button
                  variant="light"
                  color="gray"
                  size="sm"
                  rightSection={<IconChevronDown size={16} />}
                >
                  Status
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>Source status</Menu.Label>
                {otherStatuses.map((status) => (
                  <Menu.Item
                    key={status}
                    color={status === 'Active' ? undefined : 'red'}
                    onClick={() => setPendingStatus(status)}
                  >
                    {STATUS_ACTION_LABELS[status]}
                  </Menu.Item>
                ))}
                <Menu.Divider />
                <Menu.Item
                  color="red"
                  onClick={() => setPendingStatus('Delete')}
                >
                  Delete source permanently
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          )}
        </Group>
      </Group>

      <SegmentedControl
        aria-label="Source view"
        value={view}
        onChange={setView}
        data={[
          {
            value: 'entries',
            label: 'Entries & coverage',
          },
          {
            value: 'files',
            label: 'File details',
          },
        ]}
      />
      {view === 'entries' ? (
        <SourceEntryExplorer datId={dat.id} />
      ) : (
        <>
          <Group
            justify="space-between"
            align="center"
          >
            <Text
              size="sm"
              c="dimmed"
            >
              {games.length.toLocaleString()} {bios === 'Only' ? 'BIOS entries' : 'entries'} shown
            </Text>
            <SegmentedControl
              size="xs"
              value={bios}
              onChange={(value) => setBios(value as BiosFilter)}
              data={[
                {
                  label: 'Games',
                  value: 'Exclude',
                },
                {
                  label: 'All',
                  value: 'Include',
                },
                {
                  label: 'BIOS',
                  value: 'Only',
                },
              ]}
            />
          </Group>

          {isLoading ? (
            <Center py={40}>
              <Loader />
            </Center>
          ) : isError ? (
            <Text role="alert">
              Entries could not be loaded.{' '}
              <Button onClick={() => void refetch()}>Retry entries</Button>
            </Text>
          ) : (
            <GamesTable
              games={games}
              bios={bios}
              hasNextPage={hasNextPage ?? false}
              isFetchingNextPage={isFetchingNextPage}
              onLoadMore={() => fetchNextPage()}
            />
          )}
        </>
      )}
      <ConfirmSourceStatusModal
        dat={dat}
        targetStatus={pendingStatus}
        onClose={() => setPendingStatus(null)}
      />
    </Stack>
  );
}
