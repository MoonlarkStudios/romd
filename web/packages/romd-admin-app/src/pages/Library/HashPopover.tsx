import { ActionIcon, Popover, Stack, Text } from '@mantine/core';
import type { Rom } from '@romd/admin-api-client';
import { IconHash } from '@tabler/icons-react';
import { HashRow } from '../../components/HashRow';

interface HashPopoverProps {
  rom: Rom;
}

export function HashPopover({ rom }: HashPopoverProps) {
  return (
    <Popover width={320} position="left" withArrow shadow="md">
      <Popover.Target>
        <ActionIcon variant="subtle" color="gray" onClick={(e) => e.stopPropagation()} title="View hashes">
          <IconHash size={16} />
        </ActionIcon>
      </Popover.Target>
      <Popover.Dropdown onClick={(e) => e.stopPropagation()}>
        <Stack gap="xs">
          <Text size="sm" fw={500}>Checksums</Text>
          <HashRow label="SHA1" value={rom.sha1} />
          <HashRow label="MD5" value={rom.md5} />
          <HashRow label="CRC32" value={rom.crc32} />
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
