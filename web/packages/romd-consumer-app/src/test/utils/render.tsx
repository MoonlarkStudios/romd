import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type RenderOptions, render } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter, type MemoryRouterProps } from 'react-router';
import { AuthProvider } from '../../contexts/AuthContext';
import { consumerTheme } from '../../styles/theme';

interface WrapperProps {
  children: ReactNode;
  routerOptions?: Omit<MemoryRouterProps, 'children'>;
  withAuth?: boolean;
}

interface CustomRenderOptions extends Omit<RenderOptions, 'wrapper'> {
  routerOptions?: Omit<MemoryRouterProps, 'children'>;
  withAuth?: boolean;
}

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
        staleTime: 0,
      },
      mutations: {
        retry: false,
      },
    },
  });
}

function AllProviders({ children, routerOptions, withAuth = true }: WrapperProps) {
  const queryClient = createTestQueryClient();
  const content = withAuth ? <AuthProvider>{children}</AuthProvider> : children;

  return (
    <MantineProvider theme={consumerTheme}>
      <Notifications />
      <QueryClientProvider client={queryClient}>
        <MemoryRouter {...routerOptions}>{content}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

export function renderWithProviders(ui: ReactElement, options: CustomRenderOptions = {}) {
  const { routerOptions, withAuth, ...renderOptions } = options;

  return render(ui, {
    wrapper: ({ children }) => (
      <AllProviders
        routerOptions={routerOptions}
        withAuth={withAuth}
      >
        {children}
      </AllProviders>
    ),
    ...renderOptions,
  });
}

export { renderWithProviders as render };
