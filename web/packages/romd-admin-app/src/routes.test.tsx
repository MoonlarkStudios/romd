import { isValidElement } from 'react';
import { describe, expect, it } from 'vitest';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { routes } from './routes';

describe('admin routes', () => {
  it('keeps operational diagnostics behind the same Admin role as the API', () => {
    const rootRoute = routes.find((route) => route.path === '/');
    const diagnosticsRoute = rootRoute?.children?.find(
      (route) => route.path === 'diagnostics',
    );

    expect(diagnosticsRoute).toBeDefined();
    expect(isValidElement<{ requiredRoles?: string[] }>(diagnosticsRoute?.element)).toBe(true);

    if (!isValidElement<{ requiredRoles?: string[] }>(diagnosticsRoute?.element)) {
      throw new Error('Diagnostics route must render a ProtectedRoute');
    }

    expect(diagnosticsRoute.element.type).toBe(ProtectedRoute);
    expect(diagnosticsRoute.element.props.requiredRoles).toEqual(['Admin']);
  });
});
