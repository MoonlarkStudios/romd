import { MantineProvider, v8CssVariablesResolver } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { CatalogFixture } from '@romd/consumer-ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { type RenderOptions, render } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter, type MemoryRouterProps } from 'react-router';
import { AuthContext, type AuthContextValue } from '../../contexts/AuthContext';
import { theme } from '../../theme';

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
}

/**
 * Creates a new QueryClient configured for testing.
 * - Disables retries to make tests deterministic
 * - Sets gcTime and staleTime to 0 to prevent caching between tests
 */
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

/**
 * Wraps components with all necessary providers for testing.
 */
function AllProviders({ children, routerOptions, auth = signedOutAuth }: WrapperProps) {
  const queryClient = createTestQueryClient();

  return (
    <MantineProvider
      cssVariablesResolver={v8CssVariablesResolver}
      theme={theme}
      env="test"
    >
      <Notifications />
      <QueryClientProvider client={queryClient}>
        <MemoryRouter {...routerOptions}>
          <AuthContext.Provider value={auth}>
            <CatalogFixture>{children}</CatalogFixture>
          </AuthContext.Provider>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

export interface CustomRenderOptions extends Omit<RenderOptions, 'wrapper'> {
  /** Stable auth state; auth lifecycle tests should render AuthProvider directly. */
  auth?: AuthContextValue;
  /** Options passed to MemoryRouter (e.g., initialEntries, initialIndex) */
  routerOptions?: Omit<MemoryRouterProps, 'children'>;
}

/**
 * Custom render function that wraps components with all providers.
 *
 * @example
 * ```tsx
 * // Basic usage
 * render(<MyComponent />);
 *
 * // With router options
 * render(<MyComponent />, { routerOptions: { initialEntries: ['/dashboard'] } });
 * ```
 */
export function renderWithProviders(ui: ReactElement, options: CustomRenderOptions = {}) {
  const { routerOptions, auth, ...renderOptions } = options;

  return render(ui, {
    wrapper: ({ children }) => (
      <AllProviders
        routerOptions={routerOptions}
        auth={auth}
      >
        {children}
      </AllProviders>
    ),
    ...renderOptions,
  });
}

// Re-export renderWithProviders as render for convenience
export { renderWithProviders as render };
