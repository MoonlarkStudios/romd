import { Suspense } from 'react';
import { LoadingState } from './components/AsyncBoundary/LoadingState';
import '@mantine/core/styles.css';
import 'mantine-datatable/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dropzone/styles.css';
import '@mantine/spotlight/styles.css';
import '@romd/foundation/variables.css';
import '@romd/foundation/typography.css';
import '@romd/foundation/brand.css';
import '@romd/foundation/auth.css';

import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { adminCssVariablesResolver } from '@romd/foundation/theme';
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
      cssVariablesResolver={adminCssVariablesResolver}
      theme={theme}
      defaultColorScheme="dark"
    >
      <ErrorBoundary FallbackComponent={ErrorFallback}>
        <Notifications position="top-right" />
        <QueryProvider>
          <AuthProvider>
            <EffectiveReferenceCatalog>
              <Suspense fallback={<LoadingState />}>
                <RouterProvider router={router} />
              </Suspense>
            </EffectiveReferenceCatalog>
          </AuthProvider>
        </QueryProvider>
      </ErrorBoundary>
    </MantineProvider>
  );
}
