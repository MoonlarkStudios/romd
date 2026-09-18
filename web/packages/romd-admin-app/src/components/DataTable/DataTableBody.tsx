import { Table } from '@mantine/core';
import { flexRender } from '@tanstack/react-table';
import { useDataTableContext } from './DataTableContext';
import type { DataTableColumn } from './types';

export function DataTableBody() {
  const { table, onRowClick } = useDataTableContext();

  return (
    <Table.Tbody>
      {table.getRowModel().rows.map((row) => (
        <Table.Tr
          key={row.id}
          onClick={onRowClick ? () => onRowClick(row.original) : undefined}
          style={{ cursor: onRowClick ? 'pointer' : 'default' }}
        >
          {row.getVisibleCells().map((cell) => {
            const columnDef = cell.column.columnDef as DataTableColumn<unknown>;
            const align = columnDef.align ?? 'left';

            return (
              <Table.Td key={cell.id} style={{ textAlign: align }}>
                {flexRender(cell.column.columnDef.cell, cell.getContext())}
              </Table.Td>
            );
          })}
        </Table.Tr>
      ))}
    </Table.Tbody>
  );
}
