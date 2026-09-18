import { Paper, Skeleton, Stack, Table } from '@mantine/core';
import type { ReactNode } from 'react';
import { EmptyState } from '../EmptyState';
import { DataTableBody } from './DataTableBody';
import { DataTableProvider } from './DataTableContext';
import { DataTableHeader } from './DataTableHeader';
import { DataTablePagination } from './DataTablePagination';
import { DataTableToolbar } from './DataTableToolbar';
import type { DataTableContextValue, DataTableProps } from './types';
import { useDataTable } from './useDataTable';

/**
 * A feature-rich data table with sorting, pagination, search, and column visibility.
 * Built on TanStack Table with Mantine styling.
 *
 * @example
 * ```tsx
 * const columns: DataTableColumn<User>[] = [
 *   { id: 'name', header: 'Name', accessorKey: 'name' },
 *   { id: 'email', header: 'Email', accessorKey: 'email' },
 *   { id: 'role', header: 'Role', accessorKey: 'role', align: 'center' },
 * ];
 *
 * <DataTable
 *   data={users}
 *   columns={columns}
 *   getRowId={(row) => row.id}
 *   onRowClick={(user) => navigate(`/users/${user.id}`)}
 * />
 * ```
 */
export function DataTable<TData>({
  data,
  columns,
  emptyState,
  isLoading = false,
  showToolbar = true,
  toolbarActions,
  striped = true,
  highlightOnHover = true,
  layout = 'auto',
  onRowClick,
  ...tableOptions
}: DataTableProps<TData>) {
  const { table, globalFilter, setGlobalFilter, pageSizeOptions, enableSorting, enablePagination, enableColumnVisibility } =
    useDataTable({ data, columns, ...tableOptions });

  // Loading state with skeleton
  if (isLoading) {
    return (
      <Stack gap="md">
        {showToolbar && <Skeleton height={36} width={300} />}
        <Paper withBorder>
          <Table striped={striped} layout={layout}>
            <Table.Thead>
              <Table.Tr>
                {columns.map((col) => (
                  <Table.Th key={col.id}>
                    <Skeleton height={16} width={80} />
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {Array.from({ length: 5 }).map((_, i) => (
                <Table.Tr key={i}>
                  {columns.map((col) => (
                    <Table.Td key={col.id}>
                      <Skeleton height={14} />
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Paper>
      </Stack>
    );
  }

  // Empty state
  if (data.length === 0) {
    return <>{emptyState ?? <EmptyState title="No data" description="There are no items to display." />}</>;
  }

  const contextValue: DataTableContextValue<TData> = {
    table,
    columns,
    enableSorting,
    enableColumnVisibility,
    enablePagination,
    pageSizeOptions,
    onRowClick,
  };

  return (
    <DataTableProvider value={contextValue}>
      <Stack gap="md">
        {showToolbar && (
          <DataTableToolbar actions={toolbarActions} searchValue={globalFilter} onSearchChange={setGlobalFilter} />
        )}

        <Paper withBorder>
          <Table striped={striped} highlightOnHover={highlightOnHover} layout={layout}>
            <DataTableHeader />
            <DataTableBody />
          </Table>

          {enablePagination && <DataTablePagination />}
        </Paper>
      </Stack>
    </DataTableProvider>
  );
}

// Export sub-components for advanced composition
DataTable.Header = DataTableHeader;
DataTable.Body = DataTableBody;
DataTable.Pagination = DataTablePagination;
DataTable.Toolbar = DataTableToolbar;
DataTable.ColumnToggle = DataTableColumnToggle;

// Re-export for convenience
import { DataTableColumnToggle } from './DataTableColumnToggle';
