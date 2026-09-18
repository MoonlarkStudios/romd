import { createContext, useContext } from 'react';
import type { DataTableContextValue } from './types';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const DataTableContext = createContext<DataTableContextValue<any> | null>(null);

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
