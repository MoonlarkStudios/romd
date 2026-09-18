import {
  ActionIcon,
  Badge,
  Box,
  Button,
  CopyButton,
  Divider,
  Group,
  Kbd,
  Paper,
  ScrollArea,
  Stack,
  Text,
  ThemeIcon,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { Rom } from '@romd/admin-api-client';
import {
  IconArrowLeft,
  IconCheck,
  IconCircleCheck,
  IconCircleDashed,
  IconCircleX,
  IconCopy,
  IconDownload,
  IconExternalLink,
  IconStack2,
  IconTrash,
} from '@tabler/icons-react';
import { authenticatedDownload } from '../../utils/download';
import { formatBytes } from '../../utils/format';

function formatDate(dateString?: string): string {
  if (!dateString) return '-';
  return new Date(dateString).toLocaleString();
}

function getStatusConfig(status: string) {
  switch (status.toLowerCase()) {
    case 'cataloged':
      return {
        color: 'green',
        icon: IconCircleCheck,
        label: 'Cataloged',
        description: 'This ROM is matched to a DAT entry and assigned to a platform',
      };
    case 'unrouted':
      return {
        color: 'orange',
        icon: IconCircleDashed,
        label: 'Unrouted',
        description: 'Matched to a DAT but the DAT is not assigned to a platform',
      };
    case 'unidentified':
      return {
        color: 'red',
        icon: IconCircleX,
        label: 'Unidentified',
        description: 'No matching DAT entry found for this file',
      };
    default:
      return {
        color: 'gray',
        icon: IconCircleDashed,
        label: status || 'Unknown',
        description: 'Status unknown',
      };
  }
}

interface HashRowProps {
  label: string;
  value: string | null | undefined;
}

function HashRow({ label, value }: HashRowProps) {
  return (
    <Group gap="xs" justify="space-between" wrap="nowrap">
      <Text size="sm" fw={500} c="dimmed" w={50}>
        {label}
      </Text>
      {value ? (
        <Group gap={4} wrap="nowrap" flex={1} miw={0}>
          <Text
            size="sm"
            ff="monospace"
            truncate="end"
            flex={1}
            miw={0}
            title={value}
          >
            {value}
          </Text>
          <CopyButton value={value} timeout={2000}>
            {({ copied, copy }) => (
              <Tooltip label={copied ? 'Copied!' : 'Copy'} withArrow>
                <ActionIcon
                  size="xs"
                  variant="subtle"
                  color={copied ? 'teal' : 'gray'}
                  onClick={copy}
                >
                  {copied ? <IconCheck size={12} /> : <IconCopy size={12} />}
                </ActionIcon>
              </Tooltip>
            )}
          </CopyButton>
        </Group>
      ) : (
        <Text size="sm" c="dimmed">
          -
        </Text>
      )}
    </Group>
  );
}

export interface RomDetailPaneProps {
  /** Selected ROM to display */
  rom: Rom | null;
  /** Callback when back button is clicked (mobile) */
  onBack?: () => void;
  /** Callback when delete is clicked */
  onDelete?: (rom: Rom) => void;
  /** Show back button (for mobile view) */
  showBackButton?: boolean;
}

/**
 * Detail pane component showing comprehensive ROM information.
 * Displays file info, hashes, status, and actions.
 */
export function RomDetailPane({
  rom,
  onBack,
  onDelete,
  showBackButton = false,
}: RomDetailPaneProps) {
  if (!rom) {
    return (
      <Paper
        p="xl"
        style={{
          flex: 1,
          borderLeft: '2px solid var(--mantine-color-default-border)',
          backgroundColor: 'var(--mantine-color-default)',
          display: 'flex',
          flexDirection: 'column',
        }}
      >
        <Stack align="center" justify="center" h="100%" gap="md">
          <ThemeIcon size={64} radius="xl" variant="light" color="gray">
            <IconStack2 size={32} />
          </ThemeIcon>
          <Stack align="center" gap={4}>
            <Text fw={500} c="dimmed">
              Select a ROM
            </Text>
            <Text size="sm" c="dimmed" ta="center" maw={250}>
              Click a ROM or use <Kbd size="xs">j</Kbd>/<Kbd size="xs">k</Kbd> to navigate
            </Text>
          </Stack>
        </Stack>
      </Paper>
    );
  }

  const statusConfig = getStatusConfig(rom.status);
  const StatusIcon = statusConfig.icon;

  const handleDownload = () => {
    authenticatedDownload(`/api/roms/${rom.id}/download`, rom.originalFilename).catch(() => {
      notifications.show({
        title: 'Download failed',
        message: `Could not download ${rom.originalFilename}.`,
        color: 'red',
      });
    });
  };

  const handleDelete = () => {
    onDelete?.(rom);
  };

  const handleSearchOnline = () => {
    if (rom.sha1) {
      window.open(
        `https://datomatic.no-intro.org/?page=search&s=sha1:${rom.sha1}`,
        '_blank'
      );
    }
  };

  const handleCopyAllHashes = () => {
    const hashes = [
      `SHA1: ${rom.sha1 || '-'}`,
      `MD5: ${rom.md5 || '-'}`,
      `CRC32: ${rom.crc32 || '-'}`,
    ].join('\n');
    navigator.clipboard.writeText(hashes);
    notifications.show({
      title: 'Copied',
      message: 'All hashes copied to clipboard',
      color: 'teal',
    });
  };

  return (
    <Paper
      p="md"
      style={{
        flex: 1,
        borderLeft: '2px solid var(--mantine-color-default-border)',
        backgroundColor: 'var(--mantine-color-default)',
        display: 'flex',
        flexDirection: 'column',
        minHeight: 0,
      }}
    >
      <ScrollArea flex={1}>
        <Stack gap="md">
          {/* Header */}
          <Group gap="sm" wrap="nowrap">
            {showBackButton && (
              <ActionIcon variant="subtle" onClick={onBack}>
                <IconArrowLeft size={20} />
              </ActionIcon>
            )}
            <Box flex={1} miw={0}>
              <Text fw={600} size="sm" style={{ overflowWrap: 'anywhere' }}>
                {rom.originalFilename}
              </Text>
              <Text size="sm" c="dimmed">
                {formatBytes(rom.size)}
              </Text>
            </Box>
          </Group>

          <Divider />

          {/* Status */}
          <Box>
            <Text size="sm" fw={500} mb="xs">
              Status
            </Text>
            <Paper withBorder p="sm">
              <Group gap="sm" wrap="nowrap">
                <ThemeIcon
                  size="lg"
                  radius="xl"
                  color={statusConfig.color}
                  variant="light"
                >
                  <StatusIcon size={20} />
                </ThemeIcon>
                <Box flex={1}>
                  <Badge color={statusConfig.color} variant="light">
                    {statusConfig.label}
                  </Badge>
                  <Text size="xs" c="dimmed" mt={4}>
                    {statusConfig.description}
                  </Text>
                </Box>
              </Group>
            </Paper>
          </Box>

          {/* File Info */}
          <Box>
            <Text size="sm" fw={500} mb="xs">
              File Information
            </Text>
            <Stack gap="xs">
              <Group justify="space-between">
                <Text size="sm" c="dimmed">
                  Size
                </Text>
                <Text size="sm">{formatBytes(rom.size)}</Text>
              </Group>
              <Group justify="space-between">
                <Text size="sm" c="dimmed">
                  Imported
                </Text>
                <Text size="sm">{formatDate(rom.importedAt)}</Text>
              </Group>
            </Stack>
          </Box>

          {/* Hashes */}
          <Box>
            <Group justify="space-between" mb="xs">
              <Text size="sm" fw={500}>
                File Hashes
              </Text>
              <Tooltip label="Copy all hashes">
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  onClick={handleCopyAllHashes}
                >
                  <IconCopy size={14} />
                </ActionIcon>
              </Tooltip>
            </Group>
            <Paper withBorder p="sm">
              <Stack gap="xs">
                <HashRow label="SHA1" value={rom.sha1} />
                <HashRow label="MD5" value={rom.md5} />
                <HashRow label="CRC32" value={rom.crc32} />
              </Stack>
            </Paper>
          </Box>

          {/* DAT Matches - placeholder for future */}
          {/* TODO: Add DAT matches when API supports it */}

          <Divider />

          {/* Actions */}
          <Stack gap="xs">
            <Button
              leftSection={<IconDownload size={16} />}
              variant="light"
              fullWidth
              onClick={handleDownload}
            >
              Download
            </Button>

            {rom.status.toLowerCase() === 'unidentified' && (
              <Button
                leftSection={<IconExternalLink size={16} />}
                variant="light"
                color="blue"
                fullWidth
                onClick={handleSearchOnline}
              >
                Search Hash Online
              </Button>
            )}

            {onDelete && (
              <Button
                leftSection={<IconTrash size={16} />}
                variant="light"
                color="red"
                fullWidth
                onClick={handleDelete}
              >
                Delete
              </Button>
            )}
          </Stack>
        </Stack>
      </ScrollArea>
    </Paper>
  );
}

export default RomDetailPane;
