import {
  ActionIcon,
  Button,
  Group,
  Kbd,
  Paper,
  Text,
  Tooltip,
  Transition,
} from '@mantine/core';
import { useClipboard } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { Rom } from '@romd/admin-api-client';
import {
  IconCheck,
  IconClipboard,
  IconDownload,
  IconTrash,
  IconX,
} from '@tabler/icons-react';
import { useCallback } from 'react';

export interface BulkActionBarProps {
  selectedCount: number;
  totalCount: number;
  onDelete?: () => void;
  onDownload?: () => void;
  onClearSelection: () => void;
  onSelectAll: () => void;
  isDeleteLoading?: boolean;
  currentFilter?: string;
  selectedRoms?: Rom[];
}

export function BulkActionBar({
  selectedCount,
  totalCount,
  onDelete,
  onDownload,
  onClearSelection,
  onSelectAll,
  isDeleteLoading = false,
  currentFilter,
  selectedRoms = [],
}: BulkActionBarProps) {
  const isVisible = selectedCount > 0;
  const allSelected = selectedCount === totalCount && totalCount > 0;
  const clipboard = useClipboard();

  const handleExportHashes = useCallback(() => {
    const hashes = selectedRoms
      .filter((rom) => rom.sha1)
      .map((rom) => `${rom.originalFilename}\t${rom.sha1}`)
      .join('\n');

    if (hashes) {
      clipboard.copy(hashes);
      notifications.show({
        title: 'Hashes copied',
        message: `${selectedRoms.length} ROM hash(es) copied to clipboard`,
        color: 'teal',
      });
    }
  }, [selectedRoms, clipboard]);

  const showExportHashes = currentFilter === 'Unidentified';

  return (
    <Transition mounted={isVisible} transition="slide-up" duration={200}>
      {(styles) => (
        <Paper
          style={{
            ...styles,
            position: 'fixed',
            bottom: 0,
            left: 0,
            right: 0,
            zIndex: 100,
            borderTop: '1px solid var(--mantine-color-gray-light)',
            borderRadius: 0,
          }}
          shadow="lg"
          p="sm"
        >
          <Group justify="space-between" wrap="nowrap">
            <Group gap="md" wrap="nowrap">
              <Tooltip label="Clear selection (Esc)">
                <ActionIcon variant="subtle" color="gray" onClick={onClearSelection}>
                  <IconX size={18} />
                </ActionIcon>
              </Tooltip>

              <Text size="sm" fw={500}>
                {selectedCount.toLocaleString()} selected
              </Text>

              {!allSelected && (
                <Button
                  variant="subtle"
                  size="xs"
                  onClick={onSelectAll}
                  leftSection={<IconCheck size={14} />}
                >
                  Select all {totalCount.toLocaleString()}
                </Button>
              )}
            </Group>

            <Group gap="xs" wrap="nowrap">
              {showExportHashes && (
                <Tooltip label="Copy hashes for external lookup">
                  <Button
                    variant="light"
                    color="gray"
                    size="sm"
                    leftSection={<IconClipboard size={16} />}
                    onClick={handleExportHashes}
                    disabled={selectedCount === 0}
                  >
                    Export Hashes
                  </Button>
                </Tooltip>
              )}

              {onDownload && (
                <Tooltip label="Download selected ROMs">
                  <Button
                    variant="light"
                    color="blue"
                    size="sm"
                    leftSection={<IconDownload size={16} />}
                    onClick={onDownload}
                    disabled={selectedCount === 0}
                  >
                    Download
                  </Button>
                </Tooltip>
              )}

              {onDelete && (
                <Tooltip label="Delete selected ROMs">
                  <Button
                    variant="light"
                    color="red"
                    size="sm"
                    leftSection={<IconTrash size={16} />}
                    onClick={onDelete}
                    loading={isDeleteLoading}
                    disabled={selectedCount === 0}
                  >
                    <Group gap={4} wrap="nowrap">
                      Delete
                      <Kbd size="xs">D</Kbd>
                    </Group>
                  </Button>
                </Tooltip>
              )}
            </Group>
          </Group>
        </Paper>
      )}
    </Transition>
  );
}

export default BulkActionBar;
