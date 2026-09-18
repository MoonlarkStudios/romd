import { ActionIcon, Badge, Button, Group, Menu, Stack, Text } from '@mantine/core';
import { useClipboard } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { Rom } from '@romd/admin-api-client';
import {
  IconCopy,
  IconDotsVertical,
  IconDownload,
  IconExternalLink,
  IconTrash,
} from '@tabler/icons-react';
import type { SortingState } from '@tanstack/react-table';
import { useCallback, useMemo } from 'react';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn } from '../../components/DataTable/types';
import { EmptyState } from '../../components/EmptyState';
import { useDeleteRom } from '../../hooks/api/useRoms';
import { authenticatedDownload } from '../../utils/download';
import { compareIntegerValues, formatBytes } from '../../utils/format';
import { HashPopover } from './HashPopover';
import type { SortConfig } from './useLibraryFilters';

function formatDate(dateString?: string): string {
  if (!dateString) return '-';
  return new Date(dateString).toLocaleDateString();
}

function getStatusColor(status: string): string {
  switch (status) {
    case 'Cataloged':
      return 'green';
    case 'Unrouted':
      return 'orange';
    case 'Unidentified':
      return 'red';
    default:
      return 'gray';
  }
}

interface LibraryTableProps {
  /** ROM data to display */
  data: Rom[];
  /** Loading state */
  isLoading?: boolean;
  /** Search query for filtering (client-side) */
  searchQuery?: string;
  /** Current sort configuration from URL */
  sort: SortConfig | null;
  /** Handler for sort changes */
  onSortChange: (sort: SortConfig | null) => void;
  /** Whether more data is available (for Load More) */
  hasMore?: boolean;
  /** Whether more data is being loaded */
  isLoadingMore?: boolean;
  /** Handler for Load More button */
  onLoadMore?: () => void;
  /** Whether the table is showing filtered results (affects empty state) */
  hasFilters?: boolean;
}

/**
 * Library table component using DataTable with ROM-specific columns and actions.
 */
export function LibraryTable({
  data,
  isLoading = false,
  searchQuery = '',
  sort,
  onSortChange,
  hasMore = false,
  isLoadingMore = false,
  onLoadMore,
  hasFilters = false,
}: LibraryTableProps) {
  const deleteRomMutation = useDeleteRom();
  const clipboard = useClipboard();

  // Client-side search filtering
  const filteredData = useMemo(() => {
    if (!searchQuery) return data;

    const searchLower = searchQuery.toLowerCase();
    return data.filter((rom) => rom.originalFilename.toLowerCase().includes(searchLower));
  }, [data, searchQuery]);

  // Convert URL sort config to TanStack Table sorting state
  const sorting = useMemo<SortingState>(() => {
    if (!sort) return [];
    return [{ id: sort.column, desc: sort.direction === 'desc' }];
  }, [sort]);

  // Handle sorting changes from DataTable
  const handleSortingChange = useCallback(
    (newSorting: SortingState) => {
      if (newSorting.length === 0) {
        onSortChange(null);
      } else {
        const sortItem = newSorting[0];
        onSortChange({
          column: sortItem.id,
          direction: sortItem.desc ? 'desc' : 'asc',
        });
      }
    },
    [onSortChange]
  );

  const handleDownload = useCallback((rom: Rom) => {
    authenticatedDownload(`/api/roms/${rom.id}/download`, rom.originalFilename).catch(() => {
      notifications.show({
        title: 'Download failed',
        message: `Could not download ${rom.originalFilename}.`,
        color: 'red',
      });
    });
  }, []);

  const handleDelete = useCallback(
    async (rom: Rom) => {
      try {
        await deleteRomMutation.mutateAsync(rom.id);
        notifications.show({
          title: 'ROM deleted',
          message: `${rom.originalFilename} has been removed from the library.`,
          color: 'green',
        });
      } catch {
        notifications.show({
          title: 'Delete failed',
          message: 'Failed to delete ROM. Please try again.',
          color: 'red',
        });
      }
    },
    [deleteRomMutation]
  );

  const handleCopySha1 = useCallback(
    (rom: Rom) => {
      if (rom.sha1) {
        clipboard.copy(rom.sha1);
        notifications.show({
          title: 'Copied',
          message: 'SHA1 hash copied to clipboard',
          color: 'teal',
        });
      }
    },
    [clipboard]
  );

  const handleSearchHash = useCallback((rom: Rom) => {
    if (rom.sha1) {
      // Open No-Intro Datomatic search with the SHA1 hash
      window.open(`https://datomatic.no-intro.org/?page=search&s=sha1:${rom.sha1}`, '_blank');
    }
  }, []);

  const columns = useMemo<DataTableColumn<Rom>[]>(
    () => [
      {
        id: 'originalFilename',
        header: 'Filename',
        accessorKey: 'originalFilename',
        enableSorting: true,
        cell: ({ getValue }) => (
          <Text fw={500} size="sm" truncate="end" maw={300}>
            {getValue() as string}
          </Text>
        ),
      },
      {
        id: 'size',
        header: 'Size',
        accessorKey: 'size',
        align: 'right',
        enableSorting: true,
        cell: ({ getValue }) => <Text size="sm">{formatBytes(getValue<string>())}</Text>,
        sortingFn: (a, b) => compareIntegerValues(a.original.size, b.original.size),
      },
      {
        id: 'status',
        header: 'Status',
        accessorKey: 'status',
        enableSorting: true,
        cell: ({ getValue }) => {
          const status = getValue() as string;
          return (
            <Badge variant="light" size="sm" color={getStatusColor(status)}>
              {status}
            </Badge>
          );
        },
      },
      {
        id: 'importedAt',
        header: 'Imported',
        accessorKey: 'importedAt',
        enableSorting: true,
        cell: ({ getValue }) => (
          <Text size="sm" c="dimmed">
            {formatDate(getValue() as string | undefined)}
          </Text>
        ),
      },
      {
        id: 'actions',
        header: 'Actions',
        align: 'right',
        enableSorting: false,
        enableHiding: false,
        cell: ({ row }) => {
          const rom = row.original;
          const isUnidentified = rom.status === 'Unidentified';

          return (
            <Group gap="xs" justify="flex-end">
              <HashPopover rom={rom} />
              <ActionIcon
                variant="subtle"
                color="blue"
                onClick={(e) => {
                  e.stopPropagation();
                  handleDownload(rom);
                }}
                title="Download"
              >
                <IconDownload size={16} />
              </ActionIcon>
              <Menu shadow="md" width={200} position="bottom-end">
                <Menu.Target>
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    onClick={(e) => e.stopPropagation()}
                    title="More actions"
                  >
                    <IconDotsVertical size={16} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown onClick={(e) => e.stopPropagation()}>
                  <Menu.Item
                    leftSection={<IconCopy size={14} />}
                    onClick={() => handleCopySha1(rom)}
                    disabled={!rom.sha1}
                  >
                    Copy SHA1
                  </Menu.Item>
                  {isUnidentified && (
                    <Menu.Item
                      leftSection={<IconExternalLink size={14} />}
                      onClick={() => handleSearchHash(rom)}
                      disabled={!rom.sha1}
                    >
                      Search Hash Online
                    </Menu.Item>
                  )}
                  <Menu.Divider />
                  <Menu.Item
                    leftSection={<IconDownload size={14} />}
                    onClick={() => handleDownload(rom)}
                  >
                    Download
                  </Menu.Item>
                  <Menu.Item
                    color="red"
                    leftSection={<IconTrash size={14} />}
                    onClick={() => handleDelete(rom)}
                  >
                    Delete
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            </Group>
          );
        },
      },
    ],
    [handleDownload, handleDelete, handleCopySha1, handleSearchHash]
  );

  const emptyState = hasFilters ? (
    <EmptyState title="No matches" description="No ROMs match your current filters." />
  ) : (
    <EmptyState title="No ROMs" description="No ROM files have been uploaded yet." />
  );

  return (
    <Stack gap="md">
      <DataTable
        data={filteredData}
        columns={columns}
        getRowId={(row) => row.id}
        isLoading={isLoading}
        emptyState={emptyState}
        sorting={sorting}
        onSortingChange={handleSortingChange}
        enableColumnVisibility={false}
        striped
        highlightOnHover
      />

      {hasMore && (
        <Group justify="center">
          <Button variant="light" onClick={onLoadMore} loading={isLoadingMore}>
            Load More
          </Button>
        </Group>
      )}
    </Stack>
  );
}
