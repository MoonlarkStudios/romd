import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dropzone/styles.css';
import '@mantine/spotlight/styles.css';

import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { ErrorBoundary, ErrorFallback } from './components/ErrorBoundary';
import { AuthProvider } from './contexts/AuthContext';
import { EffectiveReferenceCatalog } from './providers/EffectiveReferenceCatalog';
import { QueryProvider } from './providers/QueryProvider';
import { routes } from './routes';
import { theme } from './theme';

const router = createBrowserRouter(routes);

export function App() {
  return (
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
    >
      <ErrorBoundary FallbackComponent={ErrorFallback}>
        <Notifications position="top-right" />
        <QueryProvider>
          <AuthProvider>
            <EffectiveReferenceCatalog>
            <RouterProvider router={router} />
          </EffectiveReferenceCatalog>
          </AuthProvider>
        </QueryProvider>
      </ErrorBoundary>
    </MantineProvider>
  );
}
