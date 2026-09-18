import { Group, TextInput } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { DataTableColumnToggle } from './DataTableColumnToggle';
import { useDataTableContext } from './DataTableContext';

interface DataTableToolbarProps {
  /** Custom actions to display in toolbar */
  actions?: ReactNode;
  /** Current search value */
  searchValue: string;
  /** Search change handler */
  onSearchChange: (value: string) => void;
}

export function DataTableToolbar({ actions, searchValue, onSearchChange }: DataTableToolbarProps) {
  const { enableColumnVisibility } = useDataTableContext();

  return (
    <Group justify="space-between" mb="md">
      <TextInput
        placeholder="Search..."
        leftSection={<IconSearch size={16} />}
        value={searchValue}
        onChange={(e) => onSearchChange(e.currentTarget.value)}
        style={{ width: 300 }}
      />

      <Group gap="xs">
        {actions}
        {enableColumnVisibility && <DataTableColumnToggle />}
      </Group>
    </Group>
  );
}
