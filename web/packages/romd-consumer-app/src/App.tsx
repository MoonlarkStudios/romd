import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { AuthProvider } from './contexts/AuthContext';
import { QueryProvider } from './providers/QueryProvider';
import { routes } from './routes';
import { consumerTheme } from './styles/theme';

const router = createBrowserRouter(routes);

export function App() {
  return (
    <MantineProvider
      theme={consumerTheme}
      defaultColorScheme="dark"
      forceColorScheme="dark"
    >
      <Notifications position="top-right" />
      <QueryProvider>
        <AuthProvider>
          <RouterProvider router={router} />
        </AuthProvider>
      </QueryProvider>
    </MantineProvider>
  );
}
