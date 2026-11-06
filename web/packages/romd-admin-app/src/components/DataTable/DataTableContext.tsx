import { createContext, useContext } from 'react';
import type { DataTableContextValue } from './types';

// The provider and its descendants share TData; erase it only at the context boundary.
const DataTableContext = createContext<unknown>(null);

export function DataTableProvider<TData>({
  children,
  value,
}: {
  children: React.ReactNode;
  value: DataTableContextValue<TData>;
}) {
  return <DataTableContext.Provider value={value}>{children}</DataTableContext.Provider>;
}

export function useDataTableContext<TData>(): DataTableContextValue<TData> {
  const context = useContext(DataTableContext);
  if (!context) {
    throw new Error('DataTable components must be used within DataTable');
  }
  return context as DataTableContextValue<TData>;
}
