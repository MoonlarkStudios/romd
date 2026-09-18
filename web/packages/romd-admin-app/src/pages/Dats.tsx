import { ActionIcon, Badge, Group, Select, Stack, Text, TextInput, Title, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { Dat } from '@romd/admin-api-client';
import { IconDownload, IconSearch } from '@tabler/icons-react';
import { useCallback, useMemo, useState } from 'react';
import { AsyncBoundary } from '../components/AsyncBoundary';
import { DataTable, type DataTableColumn } from '../components/DataTable';
import { EmptyState } from '../components/EmptyState';
import { downloadDatSource, useAssignDatPlatform, useDats } from '../hooks/api/useDats';
import { usePlatforms } from '../hooks/api/usePlatforms';

function formatDate(dateString?: string): string {
  if (!dateString) return '-';
  return new Date(dateString).toLocaleDateString();
}

export function Dats() {
  const [search, setSearch] = useState('');
  const [platformFilter, setPlatformFilter] = useState<string | null>(null);

  const datsQuery = useDats();
  const platformsQuery = usePlatforms();
  const assignPlatformMutation = useAssignDatPlatform();

  // Create platform lookup map
  const platformMap = useMemo(() => {
    const map = new Map<string, string>();
    if (platformsQuery.data) {
      for (const platform of platformsQuery.data) {
        map.set(platform.key, platform.name);
      }
    }
    return map;
  }, [platformsQuery.data]);

  // Create platform options for filter select
  const platformFilterOptions = useMemo(() => {
    const options = [
      { value: '', label: 'All Platforms' },
      { value: 'unassigned', label: 'Unassigned' },
    ];

    if (platformsQuery.data) {
      for (const platform of platformsQuery.data) {
        options.push({ value: platform.key, label: platform.name });
      }
    }

    return options;
  }, [platformsQuery.data]);

  // Create platform options for assignment select (no "All" or "Unassigned")
  const platformAssignOptions = useMemo(() => {
    if (!platformsQuery.data) return [];
    return platformsQuery.data.map((platform) => ({
      value: platform.key,
      label: platform.name,
    }));
  }, [platformsQuery.data]);

  const handleAssignPlatform = async (datId: string, systemKey: string | null) => {
    if (!systemKey) return;

    try {
      await assignPlatformMutation.mutateAsync({ datId, systemKey });
      notifications.show({
        title: 'Platform assigned',
        message: 'DAT file has been assigned to the platform.',
        color: 'green',
      });
    } catch {
      notifications.show({
        title: 'Assignment failed',
        message: 'Failed to assign platform. Please try again.',
        color: 'red',
      });
    }
  };

  const handleDownload = useCallback((dat: Dat) => {
    downloadDatSource(dat).catch(() => {
      notifications.show({
        title: 'Download failed',
        message: `Could not download ${dat.name}.`,
        color: 'red',
      });
    });
  }, []);

  // Filter DATs (applied to data before passing to DataTable)
  const filterDats = (dats: Dat[]) => {
    return dats.filter((dat) => {
      // Search filter
      if (search) {
        const searchLower = search.toLowerCase();
        const nameMatch = dat.name.toLowerCase().includes(searchLower);
        const descMatch = dat.description?.toLowerCase().includes(searchLower);
        if (!nameMatch && !descMatch) return false;
      }

      // Platform filter
      if (platformFilter === 'unassigned') {
        if (dat.systemKey) return false;
      } else if (platformFilter) {
        if (dat.systemKey !== platformFilter) return false;
      }

      return true;
    });
  };

  // Define columns for DataTable
  const columns: DataTableColumn<Dat>[] = useMemo(
    () => [
      {
        id: 'name',
        header: 'Name',
        accessorKey: 'name',
        cell: ({ row }) => <Text fw={500}>{row.original.name}</Text>,
      },
      {
        id: 'type',
        header: 'Type',
        accessorKey: 'type',
        cell: ({ row }) => (
          <Badge variant="light" size="sm">
            {row.original.type}
          </Badge>
        ),
      },
      {
        id: 'platform',
        header: 'Platform',
        accessorFn: (dat) => platformMap.get(dat.systemKey ?? '') ?? '',
        enableSorting: false,
        cell: ({ row }) => {
          const dat = row.original;
          if (dat.systemKey) {
            return <Text size="sm">{platformMap.get(dat.systemKey) || dat.systemKey}</Text>;
          }
          return (
            <Select
              placeholder="Assign platform..."
              data={platformAssignOptions}
              size="xs"
              style={{ width: 160 }}
              onChange={(value) => handleAssignPlatform(dat.id, value)}
              disabled={assignPlatformMutation.isPending}
            />
          );
        },
      },
      {
        id: 'gameCount',
        header: 'Games',
        accessorKey: 'gameCount',
        align: 'right',
        cell: ({ row }) => Number(row.original.gameCount || 0).toLocaleString(),
      },
      {
        id: 'romCount',
        header: 'ROMs',
        accessorKey: 'romCount',
        align: 'right',
        cell: ({ row }) => Number(row.original.romCount || 0).toLocaleString(),
      },
      {
        id: 'importedAt',
        header: 'Imported',
        accessorKey: 'importedAt',
        cell: ({ row }) => (
          <Text size="sm" c="dimmed">
            {formatDate(row.original.importedAt)}
          </Text>
        ),
      },
      {
        id: 'actions',
        header: '',
        enableSorting: false,
        enableHiding: false,
        align: 'right',
        width: 48,
        cell: ({ row }) => (
          <Tooltip label="Download source DAT">
            <ActionIcon
              variant="subtle"
              color="gray"
              aria-label={`Download ${row.original.name}`}
              onClick={() => handleDownload(row.original)}
            >
              <IconDownload size={16} />
            </ActionIcon>
          </Tooltip>
        ),
      },
    ],
    [platformMap, platformAssignOptions, assignPlatformMutation.isPending, handleDownload]
  );

  return (
    <Stack gap="lg">
      <Title order={2}>DAT Registry</Title>

      <Group>
        <TextInput
          placeholder="Search DATs..."
          leftSection={<IconSearch size={16} />}
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          style={{ flex: 1, maxWidth: 400 }}
        />
        <Select
          placeholder="Filter by platform"
          data={platformFilterOptions}
          value={platformFilter}
          onChange={setPlatformFilter}
          clearable
          style={{ width: 200 }}
        />
      </Group>

      <AsyncBoundary
        query={datsQuery}
        emptyFallback={<EmptyState title="No DAT files" description="No DAT files have been imported yet." />}
      >
        {(dats) => {
          const filteredDats = filterDats(dats);
          return (
            <>
              <DataTable
                data={filteredDats}
                columns={columns}
                getRowId={(row) => row.id}
                showToolbar={false}
                enableColumnVisibility={false}
                emptyState={
                  <EmptyState title="No matches" description="No DATs match your current filters." size="sm" />
                }
              />
              <Text size="sm" c="dimmed">
                Showing {filteredDats.length} of {dats.length} DAT files
              </Text>
            </>
          );
        }}
      </AsyncBoundary>
    </Stack>
  );
}
