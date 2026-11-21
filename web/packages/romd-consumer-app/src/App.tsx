import { MantineProvider } from '@mantine/core';
import '@romd/foundation/auth.css';
import { Notifications } from '@mantine/notifications';
import { portalCssVariablesResolver } from '@romd/foundation/theme';
import { Suspense } from 'react';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { StatusState } from './components/status/StatusState';
import { AuthProvider } from './contexts/AuthContext';
import { LibraryPublicationObserver } from './hooks/useLibraryRefresh';
import { QueryProvider } from './providers/QueryProvider';
import { routes } from './routes';
import { consumerTheme } from './styles/theme';

const router = createBrowserRouter(routes);

export function App() {
  return (
    <MantineProvider
      cssVariablesResolver={portalCssVariablesResolver}
      theme={consumerTheme}
      defaultColorScheme="dark"
      forceColorScheme="dark"
    >
      <Notifications position="top-right" />
      <QueryProvider>
        <AuthProvider>
          <LibraryPublicationObserver />
          <Suspense
            fallback={
              <StatusState
                kind="loading"
                label="Loading page"
              />
            }
          >
            <RouterProvider router={router} />
          </Suspense>
        </AuthProvider>
      </QueryProvider>
    </MantineProvider>
  );
}
