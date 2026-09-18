import { ActionIcon, Checkbox, Menu, Stack, Text } from '@mantine/core';
import { IconColumns } from '@tabler/icons-react';
import { useDataTableContext } from './DataTableContext';

export function DataTableColumnToggle() {
  const { table } = useDataTableContext();
  const allColumns = table.getAllLeafColumns().filter((col) => col.getCanHide());

  if (allColumns.length === 0) return null;

  return (
    <Menu shadow="md" width={200} closeOnItemClick={false}>
      <Menu.Target>
        <ActionIcon variant="subtle" aria-label="Toggle columns">
          <IconColumns size={18} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Toggle columns</Menu.Label>
        <Stack gap="xs" p="xs">
          {allColumns.map((column) => (
            <Checkbox
              key={column.id}
              label={
                <Text size="sm">
                  {typeof column.columnDef.header === 'string' ? column.columnDef.header : column.id}
                </Text>
              }
              checked={column.getIsVisible()}
              onChange={column.getToggleVisibilityHandler()}
              size="xs"
            />
          ))}
        </Stack>
      </Menu.Dropdown>
    </Menu>
  );
}
