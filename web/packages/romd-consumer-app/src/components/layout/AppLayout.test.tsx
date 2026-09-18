import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useLocation } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { AppLayout } from './AppLayout';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ user: { email: 'player@example.test' }, logout: vi.fn() }),
}));

function Location() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}

describe('AppLayout search', () => {
  it.each([
    { titleReturn: { href: '/', label: 'Home', section: 'home' }, active: 'Home' },
    { titleReturn: { href: '/library?q=Metroid', label: 'Browse', section: 'browse' }, active: 'Browse' },
    { titleReturn: { href: '/collections/favorites', label: 'Favorites', section: 'home' }, active: 'Home' },
    { titleReturn: undefined, active: 'Browse' },
  ])('highlights $active on a title using its return context', ({ titleReturn, active }) => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    render(<AppLayout />, { withAuth: false, routerOptions: { initialEntries: [{ pathname: '/titles/game', state: { titleReturn } }] } });
    const activeLinks = screen.getAllByRole('link', { name: active });
    expect(activeLinks.some(link => link.getAttribute('aria-current') === 'page')).toBe(true);
    expect(screen.getAllByRole('link', { name: active === 'Home' ? 'Browse' : 'Home' }).every(link => !link.hasAttribute('aria-current'))).toBe(true);
  });

  it('opens search, submits an encoded query, and leaves browsing with one search entry point', async () => {
    render(<><AppLayout /><Location /></>, { withAuth: false });
    expect(screen.queryByRole('textbox', { name: 'Search' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Search games' }));
    const input = await screen.findByRole('textbox', { name: 'Search' });
    fireEvent.change(input, { target: { value: 'Mario & Luigi' } });
    fireEvent.submit(input.closest('form')!);
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/library?q=Mario%20%26%20Luigi'));
    expect(screen.queryByRole('button', { name: 'Search games' })).not.toBeInTheDocument();
  });
});
