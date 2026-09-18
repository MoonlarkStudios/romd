import {
  ActionIcon,
  Alert,
  Anchor,
  Box,
  Button,
  Group,
  Kbd,
  Paper,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { JobDto, Rom } from '@romd/admin-api-client';
import {
  IconAlertTriangle,
  IconInfoCircle,
  IconPlus,
  IconSearch,
  IconUpload,
  IconX,
} from '@tabler/icons-react';
import { type MouseEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import { AsyncBoundary } from '../../components/AsyncBoundary';
import { EmptyState } from '../../components/EmptyState';
import { DEFAULT_ROW_HEIGHT, VirtualList } from '../../components/power-ui';
import { useJobs } from '../../hooks/api/useJobs';
import { useBatchDeleteRoms } from '../../hooks/api/useRomBatchActions';
import { useDeleteRom, useLibraryStats } from '../../hooks/api/useRoms';
import { useUploadGeneric } from '../../hooks/api/useUpload';
import {
  createFilterShortcuts,
  createListNavigationShortcuts,
  useKeyboardShortcuts,
} from '../../hooks/useKeyboardShortcuts';
import { useMultiSelect } from '../../hooks/useMultiSelect';
import { formatBytes } from '../../utils/format';
import { BulkActionBar } from './BulkActionBar';
import { FilterBar, type FilterOption } from './FilterBar';
import { JobRow } from './JobRow';
import { RomDetailPane } from './RomDetailPane';
import { RomRow } from './RomRow';
import { UploadCenterModal } from './UploadCenterModal';
import { useLibraryData } from './useLibraryData';
import { type QueueFilter, useLibraryFilters } from './useLibraryFilters';

// Combined item type for virtual list
type ListItem =
  | { type: 'rom'; data: Rom }
  | { type: 'job'; data: JobDto };

// Queue metadata for Inbox mode framing
const QUEUE_META: Record<string, { title: string; description: string; emptyMessage: string }> = {
  all: {
    title: 'ROM Library',
    description: 'All ROM files in your collection',
    emptyMessage: 'No ROM files have been uploaded yet.',
  },
  Cataloged: {
    title: 'Cataloged',
    description: 'ROMs matched to DATs and assigned to platforms',
    emptyMessage: 'No cataloged ROMs yet.',
  },
  Unrouted: {
    title: 'Unrouted',
    description: 'ROMs matched to DATs but missing platform assignment',
    emptyMessage: 'No unrouted ROMs.',
  },
  Unidentified: {
    title: 'Unidentified',
    description: 'ROMs not matched to any DAT file',
    emptyMessage: 'No unidentified ROMs.',
  },
};

// Check if we're in "Inbox mode" (filtering by a queue with action items)
function isInboxMode(status: QueueFilter): boolean {
  return ['Unidentified', 'Unrouted'].includes(status);
}

/**
 * Enhanced Library page with virtual scrolling, split-pane layout, and bulk operations.
 */
export function Library() {
  const isMobile = useMediaQuery('(max-width: 768px)');

  // Modal state
  const [uploadModalOpen, setUploadModalOpen] = useState(false);

  // Drag-and-drop state for fullscreen dropzone
  const [isDragging, setIsDragging] = useState(false);

  // Selection state
  const [selectedRomId, setSelectedRomId] = useState<string | null>(null);
  const [focusedIndex, setFocusedIndex] = useState(0);

  // Search input ref for "/" hotkey
  const searchInputRef = useRef<HTMLInputElement>(null);

  // URL-synced filters
  const { filters, searchInput, setSearchInput, setStatus } = useLibraryFilters();

  // Data fetching
  const libraryData = useLibraryData(filters.status);
  const statsQuery = useLibraryStats();
  const { data: activeJobs } = useJobs();
  const deleteRomMutation = useDeleteRom();
  const batchDeleteMutation = useBatchDeleteRoms();
  const uploadGeneric = useUploadGeneric();

  // Filter active (non-terminal) jobs
  const nonTerminalJobs = useMemo(
    () => activeJobs?.filter((j) => !j.isTerminal) ?? [],
    [activeJobs]
  );

  // Combine jobs and ROMs for virtual list
  const combinedItems = useMemo<ListItem[]>(() => {
    const items: ListItem[] = [];

    // Add active jobs at the top
    for (const j of nonTerminalJobs) {
      if (j) items.push({ type: 'job', data: j });
    }

    // Filter ROMs by search if needed
    let roms = libraryData.items.filter((rom) => rom != null);
    if (filters.search) {
      const searchLower = filters.search.toLowerCase();
      roms = roms.filter((rom) =>
        rom.originalFilename?.toLowerCase().includes(searchLower)
      );
    }

    // Add ROMs
    for (const rom of roms) {
      if (rom) items.push({ type: 'rom', data: rom });
    }

    return items;
  }, [nonTerminalJobs, libraryData.items, filters.search]);

  // Extract just ROMs for selection
  const romItems = useMemo(
    () => combinedItems.filter((item): item is { type: 'rom'; data: Rom } => item.type === 'rom'),
    [combinedItems]
  );

  // Multi-select hook
  const multiSelect = useMultiSelect({
    items: romItems.map((item) => item.data),
    getItemKey: (rom) => rom.id,
    onSelectionChange: (keys) => {
      // If only one selected, also set as the detail pane selection
      if (keys.size === 1) {
        setSelectedRomId([...keys][0]);
      }
    },
  });

  // Drag event detection for fullscreen dropzone
  useEffect(() => {
    const handleDragEnter = () => setIsDragging(true);
    const handleDragLeave = (e: DragEvent) => {
      // Only hide when leaving the window (relatedTarget is null)
      if (!e.relatedTarget) setIsDragging(false);
    };
    const handleDrop = () => setIsDragging(false);

    window.addEventListener('dragenter', handleDragEnter);
    window.addEventListener('dragleave', handleDragLeave);
    window.addEventListener('drop', handleDrop);

    return () => {
      window.removeEventListener('dragenter', handleDragEnter);
      window.removeEventListener('dragleave', handleDragLeave);
      window.removeEventListener('drop', handleDrop);
    };
  }, []);

  // Handlers
  const handleDelete = useCallback(
    async (rom: Rom) => {
      try {
        await deleteRomMutation.mutateAsync(rom.id);
        notifications.show({
          title: 'ROM deleted',
          message: `${rom.originalFilename} has been removed.`,
          color: 'green',
        });
        if (selectedRomId === rom.id) {
          setSelectedRomId(null);
        }
      } catch {
        notifications.show({
          title: 'Delete failed',
          message: 'Failed to delete ROM. Please try again.',
          color: 'red',
        });
      }
    },
    [deleteRomMutation, selectedRomId]
  );

  const handleBulkDelete = useCallback(async () => {
    if (multiSelect.selectedCount === 0) return;

    const confirmed = window.confirm(
      `Delete ${multiSelect.selectedCount} ROM(s)? This cannot be undone.`
    );
    if (!confirmed) return;

    try {
      const result = await batchDeleteMutation.mutateAsync({
        romIds: [...multiSelect.selectedKeys],
      });
      notifications.show({
        title: 'Bulk delete complete',
        message: `Deleted ${result.deletedCount} ROM(s).${
          result.failedCount > 0 ? ` ${result.failedCount} failed.` : ''
        }`,
        color: result.failedCount > 0 ? 'yellow' : 'green',
      });
      multiSelect.clearSelection();
      setSelectedRomId(null);
    } catch {
      notifications.show({
        title: 'Bulk delete failed',
        message: 'Failed to delete ROMs. Please try again.',
        color: 'red',
      });
    }
  }, [multiSelect, batchDeleteMutation]);

  const handleDrop = useCallback(
    (files: File[]) => {
      const file = files[0];
      if (!file) return;

      uploadGeneric.mutate(
        { file },
        {
          onError: (error) => {
            notifications.show({
              title: 'Upload failed',
              message: error.message,
              color: 'red',
            });
          },
        }
      );
    },
    [uploadGeneric]
  );

  const handleItemClick = useCallback(
    (item: ListItem, index: number) => {
      setFocusedIndex(index);
      if (item.type === 'rom') {
        setSelectedRomId(item.data.id);
      }
    },
    []
  );

  const handleCheckboxChange = useCallback(
    (romId: string, _checked: boolean, event: MouseEvent) => {
      multiSelect.handleClick(romId, event);
    },
    [multiSelect]
  );

  // Build filter options with counts
  const filterOptions = useMemo<FilterOption[]>(() => {
    const stats = statsQuery.data;
    return [
      {
        value: 'all',
        label: 'All',
        count: stats ? Number(stats.totalRomFiles) : undefined,
        shortcut: '1',
      },
      {
        value: 'Cataloged',
        label: 'Cataloged',
        count: stats ? Number(stats.catalogedCount) : undefined,
        color: 'green',
        shortcut: '2',
      },
      {
        value: 'Unrouted',
        label: 'Unrouted',
        count: stats ? Number(stats.unroutedCount) : undefined,
        color: 'orange',
        shortcut: '3',
      },
      {
        value: 'Unidentified',
        label: 'Unidentified',
        count: stats ? Number(stats.unidentifiedCount) : undefined,
        color: 'red',
        shortcut: '4',
      },
    ];
  }, [statsQuery.data]);

  // Keyboard shortcuts
  const filterShortcuts = useMemo(
    () =>
      createFilterShortcuts(
        ['all', 'Cataloged', 'Unrouted', 'Unidentified'] as QueueFilter[],
        setStatus
      ),
    [setStatus]
  );

  const navigationShortcuts = useMemo(
    () =>
      createListNavigationShortcuts({
        items: combinedItems,
        selectedIndex: focusedIndex,
        onSelect: (index) => {
          setFocusedIndex(index);
          const item = combinedItems[index];
          if (item?.type === 'rom') {
            setSelectedRomId(item.data.id);
          }
        },
        onOpen: (item) => {
          if (item.type === 'rom') {
            setSelectedRomId(item.data.id);
          }
        },
        onToggleSelect: (index) => {
          const item = combinedItems[index];
          if (item?.type === 'rom') {
            multiSelect.toggle(item.data.id);
          }
        },
      }),
    [combinedItems, focusedIndex, multiSelect]
  );

  const allShortcuts = useMemo(
    () => [
      ...filterShortcuts,
      ...navigationShortcuts,
      {
        key: 'd',
        meta: true,
        description: 'Delete selected',
        action: handleBulkDelete,
      },
      {
        key: 'a',
        meta: true,
        description: 'Select all',
        action: multiSelect.selectAll,
      },
      {
        key: '/',
        description: 'Focus search',
        action: () => {
          searchInputRef.current?.focus();
          searchInputRef.current?.select();
        },
      },
      {
        key: 'Escape',
        description: 'Clear search or deselect',
        action: () => {
          // If search is focused and has text, clear it
          if (
            document.activeElement === searchInputRef.current &&
            searchInput
          ) {
            setSearchInput('');
            return;
          }

          // If search is focused but empty, blur it
          if (document.activeElement === searchInputRef.current) {
            searchInputRef.current?.blur();
            return;
          }

          // Otherwise, clear selection
          multiSelect.clearSelection();
          setSelectedRomId(null);
        },
      },
    ],
    [filterShortcuts, navigationShortcuts, handleBulkDelete, multiSelect, searchInput, setSearchInput]
  );

  useKeyboardShortcuts(allShortcuts);

  // Get selected ROM for detail pane
  const selectedRom = useMemo(
    () => libraryData.items.find((rom) => rom.id === selectedRomId) ?? null,
    [libraryData.items, selectedRomId]
  );

  // Get selected ROMs for bulk actions
  const selectedRoms = useMemo(
    () => libraryData.items.filter((rom) => multiSelect.isSelected(rom.id)),
    [libraryData.items, multiSelect]
  );

  const hasFilters = filters.status !== 'all' || filters.search !== '';

  // Render item for virtual list
  const renderItem = useCallback(
    (item: ListItem, index: number, isSelected: boolean) => {
      if (item.type === 'job') {
        return <JobRow job={item.data} isSelected={isSelected} />;
      }

      return (
        <RomRow
          rom={item.data}
          isSelected={isSelected}
          isChecked={multiSelect.isSelected(item.data.id)}
          showCheckbox={multiSelect.selectedCount > 0}
          onCheckboxChange={handleCheckboxChange}
          onDelete={handleDelete}
          compact={isMobile}
        />
      );
    },
    [multiSelect, handleCheckboxChange, handleDelete, isMobile]
  );

  const getItemKey = useCallback(
    (item: ListItem) => (item.type === 'job' ? `job-${item.data.id}` : item.data.id),
    []
  );

  // Mobile: Show detail pane fullscreen when selected
  if (isMobile && selectedRomId) {
    return (
      <RomDetailPane
        rom={selectedRom}
        onBack={() => setSelectedRomId(null)}
        onDelete={handleDelete}
        showBackButton
      />
    );
  }

  return (
    <>
      {/* Fullscreen dropzone - appears when dragging files over page */}
      <Dropzone.FullScreen
        active={isDragging}
        onDrop={handleDrop}
        accept={undefined}
        loading={uploadGeneric.isPending}
      >
        <Stack align="center" justify="center" gap="md" style={{ pointerEvents: 'none' }}>
          <IconUpload size={80} stroke={1.5} color="var(--mantine-color-blue-6)" />
          <Text size="xl" fw={500}>
            Drop files to import ROMs
          </Text>
          <Text size="sm" c="dimmed">
            Drag and drop ROM files, archives, or DAT files anywhere on this page
          </Text>
        </Stack>
      </Dropzone.FullScreen>

      <Stack
        gap="lg"
        style={{
          flex: 1,
          minHeight: 0,
          overflow: 'hidden',
        }}
      >
        {/* Header */}
        <Group justify="space-between">
          <Stack gap={0}>
            <Title order={2}>
              {isInboxMode(filters.status) ? 'Inbox' : QUEUE_META[filters.status]?.title || 'ROM Library'}
            </Title>
            {filters.status !== 'all' && (
              <Text size="sm" c="dimmed">
                {QUEUE_META[filters.status]?.description}
              </Text>
            )}
          </Stack>
          <Button
            leftSection={<IconPlus size={16} />}
            onClick={() => setUploadModalOpen(true)}
          >
            Add Files
          </Button>
        </Group>

        {/* Stats */}
        <AsyncBoundary query={statsQuery} loadingMinHeight={100} emptyFallback={null}>
          {(stats) => (
            <SimpleGrid cols={{ base: 2, sm: 4 }}>
              <Paper p="md" withBorder>
                <Text size="xl" fw={700}>
                  {Number(stats.totalRomFiles || 0).toLocaleString()}
                </Text>
                <Text size="sm" c="dimmed">
                  Total ROMs
                </Text>
                <Text size="xs" c="dimmed">
                  {formatBytes(stats.totalSizeBytes || '0')}
                </Text>
              </Paper>
              <Paper p="md" withBorder>
                <Text size="xl" fw={700} c="green">
                  {Number(stats.catalogedCount || 0).toLocaleString()}
                </Text>
                <Text size="sm" c="dimmed">
                  Cataloged
                </Text>
              </Paper>
              <Paper p="md" withBorder>
                <Text size="xl" fw={700} c="orange">
                  {Number(stats.unroutedCount || 0).toLocaleString()}
                </Text>
                <Text size="sm" c="dimmed">
                  Unrouted
                </Text>
              </Paper>
              <Paper p="md" withBorder>
                <Text size="xl" fw={700} c="red">
                  {Number(stats.unidentifiedCount || 0).toLocaleString()}
                </Text>
                <Text size="sm" c="dimmed">
                  Unidentified
                </Text>
              </Paper>
            </SimpleGrid>
          )}
        </AsyncBoundary>

        {/* Filters */}
        <FilterBar
          value={filters.status}
          onChange={(value) => setStatus(value as QueueFilter)}
          options={filterOptions}
          showShortcuts={!isMobile}
        />

        {/* Unrouted Info Banner */}
        {filters.status === 'Unrouted' && (
          <Alert icon={<IconInfoCircle size={16} />} color="orange" variant="light">
            These files match known DATs but the DATs aren't assigned to a system.{' '}
            <Anchor component={Link} to="/inbox">
              Route them in the Inbox
            </Anchor>{' '}
            to catalog these files.
          </Alert>
        )}

        {/* Error State */}
        {libraryData.error && (
          <Alert
            icon={<IconAlertTriangle size={16} />}
            title="Something went wrong"
            color="red"
            variant="light"
          >
            Failed to load ROM library. Please try again later.
          </Alert>
        )}

        {/* Main Content - Split Pane */}
        {!libraryData.error && (
          <Group
            gap={0}
            align="stretch"
            wrap="nowrap"
            style={{
              flex: 1,
              minHeight: 400,
              height: '100%',
            }}
          >
            {/* List Pane */}
            <Box
              style={{
                flex: isMobile ? 1 : '0 0 60%',
                minWidth: 0,
                minHeight: 400,
                display: 'flex',
                flexDirection: 'column',
              }}
            >
              <Paper
                withBorder
                style={{
                  flex: 1,
                  display: 'flex',
                  flexDirection: 'column',
                  minHeight: 400,
                  overflow: 'hidden',
                }}
              >
                {/* Search Section */}
                <Box
                  px="md"
                  py="sm"
                  style={{
                    borderBottom: '1px solid var(--mantine-color-default-border)',
                    backgroundColor: 'var(--mantine-color-default)',
                    flexShrink: 0,
                  }}
                >
                  <TextInput
                    ref={searchInputRef}
                    placeholder="Search by filename..."
                    leftSection={<IconSearch size={16} />}
                    value={searchInput}
                    onChange={(e) => setSearchInput(e.currentTarget.value)}
                    rightSection={
                      <Group gap="xs" wrap="nowrap">
                        {searchInput && (
                          <>
                            <Text size="xs" c="dimmed">
                              {romItems.length} results
                            </Text>
                            <ActionIcon
                              size="sm"
                              variant="subtle"
                              onClick={() => {
                                setSearchInput('');
                                searchInputRef.current?.focus();
                              }}
                            >
                              <IconX size={14} />
                            </ActionIcon>
                          </>
                        )}
                        {!searchInput && <Kbd size="xs">/</Kbd>}
                      </Group>
                    }
                    rightSectionWidth={searchInput ? 100 : 30}
                    styles={{
                      input: {
                        backgroundColor: 'var(--mantine-color-body)',
                        border: 'none',
                      },
                    }}
                  />
                </Box>

                {/* Column Header */}
                <Group
                  gap="sm"
                  wrap="nowrap"
                  px="md"
                  py="xs"
                  style={{
                    borderBottom: '1px solid var(--mantine-color-default-border)',
                    backgroundColor: 'var(--mantine-color-default)',
                    flexShrink: 0,
                  }}
                >
                  {multiSelect.selectedCount > 0 && <Box w={20} />}
                  <Text size="xs" fw={700} c="dimmed" tt="uppercase" flex={1}>
                    Filename
                  </Text>
                  <Text size="xs" fw={700} c="dimmed" tt="uppercase" w={80} ta="right">
                    Size
                  </Text>
                  <Text size="xs" fw={700} c="dimmed" tt="uppercase" w={100}>
                    Status
                  </Text>
                  {!isMobile && (
                    <Text size="xs" fw={700} c="dimmed" tt="uppercase" w={90}>
                      Imported
                    </Text>
                  )}
                  <Box w={100} />
                </Group>

                {/* Virtual List Container - use position relative/absolute for proper sizing */}
                <Box style={{ flex: 1, minHeight: 200, position: 'relative' }}>
                  <Box style={{ position: 'absolute', inset: 0, overflow: 'hidden' }}>
                    <VirtualList
                      items={combinedItems}
                      rowHeight={DEFAULT_ROW_HEIGHT}
                      getItemKey={getItemKey}
                      renderItem={renderItem}
                      selectedKey={selectedRomId}
                      isLoading={libraryData.isLoading}
                      emptyContent={
                        searchInput ? (
                          <EmptyState
                            title="No matches"
                            description={`No ROMs found matching "${searchInput}"`}
                          />
                        ) : isInboxMode(filters.status) ? (
                          <EmptyState
                            title="You're caught up!"
                            description={QUEUE_META[filters.status]?.emptyMessage || 'No items in this queue.'}
                          />
                        ) : hasFilters ? (
                          <EmptyState
                            title="No matches"
                            description={QUEUE_META[filters.status]?.emptyMessage || 'No ROMs match your current filters.'}
                          />
                        ) : (
                          <EmptyState
                            title="No ROMs"
                            description="No ROM files have been uploaded yet."
                          />
                        )
                      }
                      height="100%"
                      onItemClick={handleItemClick}
                      onScrollEnd={libraryData.hasMore ? libraryData.loadMore : undefined}
                      striped
                      highlightOnHover
                    />
                  </Box>
                </Box>

                {/* Footer - Inside Panel */}
                <Group
                  justify="space-between"
                  px="md"
                  py="xs"
                  style={{
                    borderTop: '1px solid var(--mantine-color-default-border)',
                    backgroundColor: 'var(--mantine-color-body)',
                    flexShrink: 0,
                  }}
                >
                  <Text size="xs" c="dimmed">
                    {romItems.length.toLocaleString()} ROMs
                    {libraryData.hasMore && ' • Scroll for more'}
                  </Text>
                  {multiSelect.selectedCount > 0 && (
                    <Text size="xs" c="blue">
                      {multiSelect.selectedCount} selected
                    </Text>
                  )}
                </Group>
              </Paper>
            </Box>

            {/* Detail Pane (Desktop only) */}
            {!isMobile && (
              <Box
                style={{
                  flex: '0 0 40%',
                  minWidth: 0,
                  minHeight: 0,
                  display: 'flex',
                }}
              >
                <RomDetailPane rom={selectedRom} onDelete={handleDelete} />
              </Box>
            )}
          </Group>
        )}
      </Stack>

      {/* Bulk Action Bar */}
      <BulkActionBar
        selectedCount={multiSelect.selectedCount}
        totalCount={romItems.length}
        onDelete={handleBulkDelete}
        onClearSelection={multiSelect.clearSelection}
        onSelectAll={multiSelect.selectAll}
        isDeleteLoading={batchDeleteMutation.isPending}
        currentFilter={filters.status !== 'all' ? filters.status : undefined}
        selectedRoms={selectedRoms}
      />

      {/* Upload Modal (fallback for file picker) */}
      <UploadCenterModal
        opened={uploadModalOpen}
        onClose={() => setUploadModalOpen(false)}
      />
    </>
  );
}
