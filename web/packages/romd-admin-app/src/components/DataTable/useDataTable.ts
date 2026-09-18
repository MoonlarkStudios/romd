import {
  getCoreRowModel,
  getFilteredRowModel,
  getPaginationRowModel,
  getSortedRowModel,
  type PaginationState,
  type RowSelectionState,
  type SortingState,
  useReactTable,
  type VisibilityState,
} from '@tanstack/react-table';
import { useCallback, useMemo, useState } from 'react';
import type { DataTableColumn, DataTableProps } from './types';

export function useDataTable<TData>({
  data,
  columns,
  getRowId,
  enableSorting = true,
  initialSorting = [],
  sorting: controlledSorting,
  onSortingChange,
  enablePagination,
  pageSizeOptions = [10, 25, 50, 100],
  initialPageSize = 25,
  pagination: controlledPagination,
  onPaginationChange,
  rowCount,
  enableColumnVisibility = true,
  initialColumnVisibility = {},
  columnVisibility: controlledColumnVisibility,
  onColumnVisibilityChange,
  enableRowSelection = false,
  onRowSelectionChange,
}: Omit<DataTableProps<TData>, 'emptyState' | 'isLoading' | 'showToolbar' | 'toolbarActions' | 'striped' | 'highlightOnHover' | 'layout' | 'onRowClick'>) {
  // Auto-enable pagination for large datasets
  const shouldPaginate = enablePagination ?? data.length > 20;

  // Internal state (used when not controlled)
  const [internalSorting, setInternalSorting] = useState<SortingState>(initialSorting);
  const [internalPagination, setInternalPagination] = useState<PaginationState>({
    pageIndex: 0,
    pageSize: initialPageSize,
  });
  const [internalColumnVisibility, setInternalColumnVisibility] = useState<VisibilityState>(initialColumnVisibility);
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
  const [globalFilter, setGlobalFilter] = useState('');

  // Use controlled or internal state
  const sorting = controlledSorting ?? internalSorting;
  const pagination = controlledPagination ?? internalPagination;
  const columnVisibility = controlledColumnVisibility ?? internalColumnVisibility;

  // Convert our column type to TanStack Table columns
  const tableColumns = useMemo(() => {
    return columns.map((col) => ({
      ...col,
      enableSorting: col.enableSorting ?? enableSorting,
      enableHiding: col.enableHiding ?? enableColumnVisibility,
    }));
  }, [columns, enableSorting, enableColumnVisibility]);

  const handleSortingChange = useCallback(
    (updater: SortingState | ((old: SortingState) => SortingState)) => {
      const newValue = typeof updater === 'function' ? updater(sorting) : updater;
      onSortingChange?.(newValue);
      if (!controlledSorting) setInternalSorting(newValue);
    },
    [sorting, onSortingChange, controlledSorting]
  );

  const handlePaginationChange = useCallback(
    (updater: PaginationState | ((old: PaginationState) => PaginationState)) => {
      const newValue = typeof updater === 'function' ? updater(pagination) : updater;
      onPaginationChange?.(newValue);
      if (!controlledPagination) setInternalPagination(newValue);
    },
    [pagination, onPaginationChange, controlledPagination]
  );

  const handleColumnVisibilityChange = useCallback(
    (updater: VisibilityState | ((old: VisibilityState) => VisibilityState)) => {
      const newValue = typeof updater === 'function' ? updater(columnVisibility) : updater;
      onColumnVisibilityChange?.(newValue);
      if (!controlledColumnVisibility) setInternalColumnVisibility(newValue);
    },
    [columnVisibility, onColumnVisibilityChange, controlledColumnVisibility]
  );

  const table = useReactTable({
    data,
    columns: tableColumns,
    state: {
      sorting,
      pagination,
      columnVisibility,
      rowSelection,
      globalFilter,
    },
    onSortingChange: handleSortingChange,
    onPaginationChange: handlePaginationChange,
    onColumnVisibilityChange: handleColumnVisibilityChange,
    onRowSelectionChange: (updater) => {
      const newValue = typeof updater === 'function' ? updater(rowSelection) : updater;
      setRowSelection(newValue);

      if (onRowSelectionChange) {
        // Get selected rows after state update
        const selectedRowIds = Object.keys(newValue).filter((key) => newValue[key]);
        const selectedRows = data.filter((row, index) => {
          const rowId = getRowId ? getRowId(row) : String(index);
          return selectedRowIds.includes(rowId);
        });
        onRowSelectionChange(selectedRows);
      }
    },
    onGlobalFilterChange: setGlobalFilter,
    getRowId,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getPaginationRowModel: shouldPaginate ? getPaginationRowModel() : undefined,
    manualPagination: rowCount !== undefined,
    rowCount,
    enableRowSelection,
    enableGlobalFilter: true,
  });

  return {
    table,
    globalFilter,
    setGlobalFilter,
    pageSizeOptions,
    enableSorting,
    enablePagination: shouldPaginate,
    enableColumnVisibility,
  };
}
