import { render, screen } from '@testing-library/react';
import { isValidElement, type ReactNode, Suspense } from 'react';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { routes } from './routes';

describe('admin routes', () => {
  it('keeps descriptor repair behind the Admin role', () => {
    const route = routes
      .find((r) => r.path === '/')
      ?.children?.find((r) => r.path === 'disc-layouts');
    if (
      !isValidElement<{
        requiredRoles?: string[];
      }>(route?.element)
    )
      throw new Error('Missing descriptor route');
    expect(route.element.type).toBe(ProtectedRoute);
    expect(route.element.props.requiredRoles).toEqual([
      'Admin',
    ]);
  });
  it('keeps operational diagnostics behind the same Admin role as the API', () => {
    const rootRoute = routes.find((route) => route.path === '/');
    const diagnosticsRoute = rootRoute?.children?.find((route) => route.path === 'diagnostics');

    expect(diagnosticsRoute).toBeDefined();
    expect(
      isValidElement<{
        requiredRoles?: string[];
      }>(diagnosticsRoute?.element),
    ).toBe(true);

    if (
      !isValidElement<{
        requiredRoles?: string[];
      }>(diagnosticsRoute?.element)
    ) {
      throw new Error('Diagnostics route must render a ProtectedRoute');
    }

    expect(diagnosticsRoute.element.type).toBe(ProtectedRoute);
    expect(diagnosticsRoute.element.props.requiredRoles).toEqual([
      'Admin',
    ]);
  });
});

vi.mock('./components/auth/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: ReactNode }) => children,
}));
vi.mock('./components/layout/AppLayout', () => ({
  AppLayout: () => (
    <>
      <nav>Application navigation</nav>
      <Suspense fallback={<p>Loading page</p>}>
        <Outlet />
      </Suspense>
    </>
  ),
}));
vi.mock('./pages/Dashboard/index', () => ({
  Dashboard: () => <h1>Dashboard page</h1>,
}));

it('loads a page on direct navigation through its lazy route', async () => {
  const router = createMemoryRouter(routes, {
    initialEntries: [
      '/',
    ],
  });
  render(<RouterProvider router={router} />);

  expect(screen.getByRole('navigation')).toHaveTextContent('Application navigation');
  expect(
    await screen.findByRole('heading', {
      name: 'Dashboard page',
    }),
  ).toBeInTheDocument();
  expect(screen.getByRole('navigation')).toBeInTheDocument();
});
