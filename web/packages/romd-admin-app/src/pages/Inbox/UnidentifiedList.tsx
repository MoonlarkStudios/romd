import { Alert, Box, Button, Group, Paper, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { Rom } from '@romd/admin-api-client';
import { useNavigate, useSearchParams } from 'react-router';
import { EmptyState } from '../../components/EmptyState';
import { DEFAULT_ROW_HEIGHT, VirtualList } from '../../components/power-ui';
import { useDeleteRom } from '../../hooks/api/useRoms';
import { usePermissions } from '../../hooks/usePermissions';
import { RomRow } from '../Library/RomRow';
import { useLibraryData } from '../Library/useLibraryData';

/**
 * The unidentified-files tray: stored ROMs that matched no DAT entry.
 * Reuses the Library row/detail components; heavy bulk operations stay
 * on the ROM Library page.
 */
export function UnidentifiedList() {
  const { canManageTitles } = usePermissions();
  const isMobile = useMediaQuery('(max-width: 768px)');
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const libraryData = useLibraryData('Unidentified');
  const deleteRomMutation = useDeleteRom();


  const handleDelete = async (rom: Rom) => {
    if (!canManageTitles || !window.confirm(`Delete ${rom.originalFilename}? This cannot be undone.`)) return;
    try {
      await deleteRomMutation.mutateAsync(rom.id);
      notifications.show({
        title: 'File deleted',
        message: `${rom.originalFilename} has been removed.`,
        color: 'green',
      });
    } catch {
      notifications.show({
        title: 'Delete failed',
        message: 'Failed to delete the file. Please try again.',
        color: 'red',
      });
    }
  };

  return (
    <Paper withBorder radius="md" style={{ overflow: 'hidden' }}>
      {libraryData.isError && <Alert color="red">Unidentified files could not be loaded.<Button variant="subtle" onClick={() => void libraryData.refetch()}>Retry files</Button></Alert>}
      <Group gap={0} align="stretch" wrap="nowrap" style={{ height: 420 }}>
        <Box style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          <Box style={{ flex: 1, position: 'relative' }}>
            <Box style={{ position: 'absolute', inset: 0, overflow: 'hidden' }}>
              <VirtualList
                items={libraryData.items}
                rowHeight={DEFAULT_ROW_HEIGHT}
                getItemKey={(rom: Rom) => rom.id}
                renderItem={(rom: Rom, _index: number, isSelected: boolean) => (
                  <RomRow rom={rom} isSelected={isSelected} onDelete={canManageTitles ? handleDelete : undefined} compact={isMobile} />
                )}
                isLoading={libraryData.isLoading}
                emptyContent={
                  !libraryData.isError && <EmptyState
                    title="Everything routed itself"
                    description="No stored files are waiting for identification."
                  />
                }
                height="100%"
                onItemClick={(rom: Rom) => { const next = new URLSearchParams(params); next.set('tab', 'attention'); navigate(`/roms/${rom.id}?${next}`); }}
                onScrollEnd={libraryData.hasMore ? libraryData.loadMore : undefined}
                striped
                highlightOnHover
              />
            </Box>
          </Box>
          <Group
            justify="space-between"
            px="md"
            py="xs"
            style={{ borderTop: '1px solid var(--mantine-color-default-border)', flexShrink: 0 }}
          >
            <Text size="xs" c="dimmed">
              {libraryData.items.length.toLocaleString()} files
              {libraryData.hasMore && ' • Scroll for more'}
            </Text>
          </Group>
        </Box>

      </Group>
    </Paper>
  );
}
