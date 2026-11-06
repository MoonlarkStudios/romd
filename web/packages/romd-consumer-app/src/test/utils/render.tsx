import { MantineProvider, v8CssVariablesResolver } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type RenderOptions, render } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter, type MemoryRouterProps } from 'react-router';
import { AuthContext, type AuthContextValue } from '../../contexts/AuthContext';
import { consumerTheme } from '../../styles/theme';

const signedOutAuth: AuthContextValue = {
  user: null,
  isAuthenticated: false,
  isLoading: false,
  error: null,
  retry: async () => {},
  login: async () => {},
  logout: async () => {},
};

interface WrapperProps {
  children: ReactNode;
  auth?: AuthContextValue;
  routerOptions?: Omit<MemoryRouterProps, 'children'>;
  withAuth?: boolean;
}

interface CustomRenderOptions extends Omit<RenderOptions, 'wrapper'> {
  /** Stable auth state; auth lifecycle tests should render AuthProvider directly. */
  auth?: AuthContextValue;
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

function AllProviders({
  children,
  routerOptions,
  withAuth = true,
  auth = signedOutAuth,
}: WrapperProps) {
  const queryClient = createTestQueryClient();
  const content = withAuth ? (
    <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>
  ) : (
    children
  );

  return (
    <MantineProvider
      cssVariablesResolver={v8CssVariablesResolver}
      theme={consumerTheme}
      env="test"
    >
      <Notifications />
      <QueryClientProvider client={queryClient}>
        <MemoryRouter {...routerOptions}>{content}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

export function renderWithProviders(ui: ReactElement, options: CustomRenderOptions = {}) {
  const { routerOptions, withAuth, auth, ...renderOptions } = options;

  return render(ui, {
    wrapper: ({ children }) => (
      <AllProviders
        routerOptions={routerOptions}
        auth={auth}
        withAuth={withAuth}
      >
        {children}
      </AllProviders>
    ),
    ...renderOptions,
  });
}

export { renderWithProviders as render };
