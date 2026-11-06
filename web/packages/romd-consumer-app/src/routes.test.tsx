import { render, screen } from '@testing-library/react';
import { type ReactNode, Suspense } from 'react';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router';
import { expect, it, vi } from 'vitest';
import { routes } from './routes';

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
vi.mock('./pages/Connections', () => ({
  Connections: () => <h1>Connections page</h1>,
}));

it('loads a page on direct navigation through its lazy route', async () => {
  const router = createMemoryRouter(routes, {
    initialEntries: [
      '/connections',
    ],
  });
  render(<RouterProvider router={router} />);

  expect(screen.getByRole('navigation')).toHaveTextContent('Application navigation');
  expect(
    await screen.findByRole('heading', {
      name: 'Connections page',
    }),
  ).toBeInTheDocument();
  expect(screen.getByRole('navigation')).toBeInTheDocument();
});
