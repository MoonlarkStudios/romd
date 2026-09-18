import type { ColumnDef, PaginationState, SortingState, Table, VisibilityState } from '@tanstack/react-table';
import type { ReactNode } from 'react';

/**
 * Extended column definition with additional UI properties.
 */
export type DataTableColumn<TData> = ColumnDef<TData, unknown> & {
  /** Unique column identifier */
  id: string;
  /** Column header label (string for simplicity) */
  header: string;
  /** Cell text alignment */
  align?: 'left' | 'center' | 'right';
  /** Column width (CSS value) */
  width?: number | string;
  /** Whether sorting is enabled for this column */
  enableSorting?: boolean;
  /** Whether hiding is enabled for this column */
  enableHiding?: boolean;
};

export interface DataTableProps<TData> {
  /** Data array to display */
  data: TData[];
  /** Column definitions */
  columns: DataTableColumn<TData>[];
  /** Function to get unique row ID */
  getRowId?: (row: TData) => string;

  // Sorting
  /** Enable sorting (default: true) */
  enableSorting?: boolean;
  /** Initial sorting state */
  initialSorting?: SortingState;
  /** Controlled sorting state */
  sorting?: SortingState;
  /** Sorting change handler for controlled mode */
  onSortingChange?: (sorting: SortingState) => void;

  // Pagination
  /** Enable pagination (default: true when data.length > 20) */
  enablePagination?: boolean;
  /** Page size options */
  pageSizeOptions?: number[];
  /** Initial page size */
  initialPageSize?: number;
  /** Controlled pagination state */
  pagination?: PaginationState;
  /** Pagination change handler for controlled mode */
  onPaginationChange?: (pagination: PaginationState) => void;
  /** Total row count (for server-side pagination) */
  rowCount?: number;

  // Column visibility
  /** Enable column visibility toggle (default: true) */
  enableColumnVisibility?: boolean;
  /** Initial column visibility */
  initialColumnVisibility?: VisibilityState;
  /** Controlled column visibility */
  columnVisibility?: VisibilityState;
  /** Column visibility change handler for controlled mode */
  onColumnVisibilityChange?: (visibility: VisibilityState) => void;

  // Row selection
  /** Enable row selection */
  enableRowSelection?: boolean;
  /** Selection change handler */
  onRowSelectionChange?: (selectedRows: TData[]) => void;

  // Row click
  /** Handler for row click */
  onRowClick?: (row: TData) => void;

  // UI
  /** Show toolbar with search and column toggle */
  showToolbar?: boolean;
  /** Custom toolbar actions */
  toolbarActions?: ReactNode;
  /** Custom empty state */
  emptyState?: ReactNode;
  /** Loading state */
  isLoading?: boolean;
  /** Striped rows */
  striped?: boolean;
  /** Highlight on hover */
  highlightOnHover?: boolean;
  /** Table layout */
  layout?: 'auto' | 'fixed';
}

export interface DataTableContextValue<TData> {
  table: Table<TData>;
  columns: DataTableColumn<TData>[];
  enableSorting: boolean;
  enableColumnVisibility: boolean;
  enablePagination: boolean;
  pageSizeOptions: number[];
  onRowClick?: (row: TData) => void;
}
