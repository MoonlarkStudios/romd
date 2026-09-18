import { Group, Table, Text, UnstyledButton } from '@mantine/core';
import { IconChevronDown, IconChevronUp, IconSelector } from '@tabler/icons-react';
import { flexRender } from '@tanstack/react-table';
import { useDataTableContext } from './DataTableContext';
import type { DataTableColumn } from './types';

export function DataTableHeader() {
  const { table, enableSorting } = useDataTableContext();

  return (
    <Table.Thead>
      {table.getHeaderGroups().map((headerGroup) => (
        <Table.Tr key={headerGroup.id}>
          {headerGroup.headers.map((header) => {
            const columnDef = header.column.columnDef as DataTableColumn<unknown>;
            const canSort = enableSorting && header.column.getCanSort();
            const sorted = header.column.getIsSorted();
            const align = columnDef.align ?? 'left';

            const SortIcon = sorted === 'asc' ? IconChevronUp : sorted === 'desc' ? IconChevronDown : IconSelector;

            const content = header.isPlaceholder
              ? null
              : flexRender(header.column.columnDef.header, header.getContext());

            return (
              <Table.Th
                key={header.id}
                style={{
                  width: columnDef.width,
                  textAlign: align,
                }}
              >
                {canSort ? (
                  <UnstyledButton
                    onClick={header.column.getToggleSortingHandler()}
                    style={{ display: 'flex', alignItems: 'center', gap: 4 }}
                  >
                    <Text fw={500} size="sm">
                      {content}
                    </Text>
                    <SortIcon size={14} stroke={1.5} style={{ opacity: sorted ? 1 : 0.5 }} />
                  </UnstyledButton>
                ) : (
                  <Text fw={500} size="sm">
                    {content}
                  </Text>
                )}
              </Table.Th>
            );
          })}
        </Table.Tr>
      ))}
    </Table.Thead>
  );
}
