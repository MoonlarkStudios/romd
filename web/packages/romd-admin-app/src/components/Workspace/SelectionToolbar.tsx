import { ActionIcon, Group, Text, Tooltip } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import classes from './WorkspaceTable.module.css';

export function SelectionToolbar({ count, disabled, onClear, children }: {
  count: number; disabled?: boolean; onClear: () => void; children: ReactNode;
}) {
  return <div className={classes.bulk} role="region" aria-label="Selection actions">
    <Group justify="space-between">
      <Text size="sm">{count} selected</Text>
      <Group gap="xs">
        {children}
        <Tooltip label="Clear selection"><ActionIcon aria-label="Clear selection" variant="subtle" color="gray" disabled={disabled} onClick={onClear}><IconX size={16} /></ActionIcon></Tooltip>
      </Group>
    </Group>
  </div>;
}
