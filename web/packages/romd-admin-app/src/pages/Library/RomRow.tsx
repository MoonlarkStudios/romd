import { ActionIcon, Badge, Checkbox, Group, Menu, Text, Tooltip } from '@mantine/core';
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
import { type MouseEvent, useCallback } from 'react';
import { authenticatedDownload } from '../../utils/download';
import { formatBytes } from '../../utils/format';
import { HashPopover } from './HashPopover';

function formatDate(dateString?: string): string {
  if (!dateString) return '-';
  return new Date(dateString).toLocaleDateString();
}

function getStatusColor(status: string): string {
  switch (status.toLowerCase()) {
    case 'cataloged':
      return 'green';
    case 'unrouted':
      return 'orange';
    case 'unidentified':
      return 'red';
    default:
      return 'gray';
  }
}

interface NextAction {
  label: string;
  color: string;
}

function getNextAction(status: string): NextAction | null {
  const statusLower = status.toLowerCase();

  if (statusLower === 'unidentified') {
    return { label: 'Lookup Hash', color: 'gray' };
  }
  if (statusLower === 'unrouted') {
    return { label: 'Route', color: 'orange' };
  }
  return null;
}

export interface RomRowProps {
  rom: Rom;
  isSelected?: boolean;
  isChecked?: boolean;
  showCheckbox?: boolean;
  onCheckboxChange?: (romId: string, checked: boolean, event: MouseEvent) => void;
  onDelete?: (rom: Rom) => void;
  compact?: boolean;
}

export function RomRow({
  rom,
  isSelected = false,
  isChecked = false,
  showCheckbox = false,
  onCheckboxChange,
  onDelete,
  compact = false,
}: RomRowProps) {
  const clipboard = useClipboard();

  const handleDownload = useCallback(
    (e: MouseEvent) => {
      e.stopPropagation();
      authenticatedDownload(`/api/roms/${rom.id}/download`, rom.originalFilename).catch(() => {
        notifications.show({
          title: 'Download failed',
          message: `Could not download ${rom.originalFilename}.`,
          color: 'red',
        });
      });
    },
    [rom.id, rom.originalFilename]
  );

  const handleDelete = useCallback(
    (e: MouseEvent) => {
      e.stopPropagation();
      onDelete?.(rom);
    },
    [rom, onDelete]
  );

  const handleCopySha1 = useCallback(
    (e: MouseEvent) => {
      e.stopPropagation();
      if (rom.sha1) {
        clipboard.copy(rom.sha1);
        notifications.show({
          title: 'Copied',
          message: 'SHA1 hash copied to clipboard',
          color: 'teal',
        });
      }
    },
    [clipboard, rom.sha1]
  );

  const handleSearchHash = useCallback(
    (e: MouseEvent) => {
      e.stopPropagation();
      if (rom.sha1) {
        window.open(
          `https://datomatic.no-intro.org/?page=search&s=sha1:${rom.sha1}`,
          '_blank'
        );
      }
    },
    [rom.sha1]
  );

  const handleCheckboxClick = useCallback(
    (e: MouseEvent<HTMLInputElement>) => {
      e.stopPropagation();
      onCheckboxChange?.(rom.id, !isChecked, e);
    },
    [rom.id, isChecked, onCheckboxChange]
  );

  const isUnidentified = rom.status.toLowerCase() === 'unidentified';
  const nextAction = getNextAction(rom.status);

  return (
    <Group
      gap="sm"
      wrap="nowrap"
      px="md"
      h="100%"
      align="center"
      style={{
        borderBottom: '1px solid var(--mantine-color-default-border)',
        transition: 'background-color 150ms ease',
      }}
    >
      {showCheckbox && (
        <Checkbox
          checked={isChecked}
          onChange={() => {}}
          onClick={handleCheckboxClick}
          size="sm"
          styles={{ input: { cursor: 'pointer' } }}
        />
      )}

      <Text fw={500} size="sm" truncate="end" flex={1} miw={0} maw={400} title={rom.originalFilename}>
        {rom.originalFilename}
      </Text>

      <Text size="sm" c="dimmed" w={80} ta="right">
        {formatBytes(rom.size)}
      </Text>

      <Badge
        variant="dot"
        size="sm"
        color={getStatusColor(rom.status)}
        w={100}
        styles={{ root: { textTransform: 'capitalize', fontWeight: 500 } }}
      >
        {rom.status}
      </Badge>

      {nextAction && (
        <Tooltip label={`Suggested action: ${nextAction.label}`}>
          <Badge
            variant="light"
            size="xs"
            color={nextAction.color}
            w={70}
            styles={{ root: { textTransform: 'none', fontWeight: 500, cursor: 'default' } }}
          >
            {nextAction.label}
          </Badge>
        </Tooltip>
      )}

      {!compact && (
        <Group gap={4} wrap="nowrap">
          <ActionIcon variant="subtle" size="sm" onClick={handleDownload}>
            <IconDownload size={16} />
          </ActionIcon>
          <ActionIcon variant="subtle" size="sm" onClick={handleCopySha1}>
            <IconCopy size={16} />
          </ActionIcon>
          {!isUnidentified && (
            <ActionIcon variant="subtle" size="sm" onClick={handleSearchHash}>
              <IconExternalLink size={16} />
            </ActionIcon>
          )}
        </Group>
      )}

      <Menu withinPortal position="bottom-end" shadow="md">
        <Menu.Target>
          <ActionIcon variant="subtle" size="sm">
            <IconDotsVertical size={16} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item leftSection={<IconDownload size={14} />} onClick={handleDownload}>
            Download
          </Menu.Item>
          <Menu.Item leftSection={<IconCopy size={14} />} onClick={handleCopySha1}>
            Copy SHA1
          </Menu.Item>
          {!isUnidentified && (
            <Menu.Item leftSection={<IconExternalLink size={14} />} onClick={handleSearchHash}>
              Search Hash
            </Menu.Item>
          )}
          <Menu.Divider />
          {onDelete && <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={handleDelete}>
            Delete
          </Menu.Item>}
        </Menu.Dropdown>
      </Menu>
    </Group>
  );
}
