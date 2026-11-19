import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { type ReactNode, useEffect } from 'react';
import { shareInvalidationsAcrossTabs } from '../hooks/api/titleCache';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 1000 * 60 * 5, // 5 minutes
      retry: 1,
    },
  },
});

interface QueryProviderProps {
  children: ReactNode;
}

export function QueryProvider({ children }: QueryProviderProps) {
  // Changes made in one admin tab refresh the same title surfaces in the others.
  useEffect(() => shareInvalidationsAcrossTabs(queryClient), []);
  return (
    <QueryClientProvider client={queryClient}>
      {children}
      {import.meta.env.DEV && (
        // Keep development controls below application dialogs.
        <div
          style={{
            isolation: 'isolate',
          }}
        >
          <ReactQueryDevtools initialIsOpen={false} />
        </div>
      )}
    </QueryClientProvider>
  );
}
