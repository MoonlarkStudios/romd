import { fireEvent, screen } from '@testing-library/react';
import { useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { TitleLink } from './TitleLink';
import { CollectionNavigationName, readReturnContext, titleOrigin } from './titleNavigation';

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{JSON.stringify({ path: location.pathname, state: location.state })}</output>;
}

describe('title return context', () => {
  it.each([null, {}, { titleReturn: { href: '//outside.test' } }, { titleReturn: { href: '/titles/other' } }, { titleReturn: { href: '/library/../login' } }])('falls back to Browse for missing or invalid history state: %j', state => {
    expect(readReturnContext(state)).toEqual({ href: '/library', label: 'Browse', section: 'browse' });
  });

  it('preserves filtered Browse and treats activity as Home', () => {
    expect(titleOrigin({ pathname: '/library', search: '?q=Mario&systemKey=snes', hash: '', state: null }, null)).toMatchObject({ href: '/library?q=Mario&systemKey=snes', section: 'browse' });
    expect(titleOrigin({ pathname: '/activity', search: '', hash: '', state: null }, null)).toMatchObject({ href: '/', section: 'home' });
  });

  it('does not restore an activity page scroll position onto Home', () => {
    render(<><TitleLink to="/titles/game">Details</TitleLink><LocationProbe /></>, { withAuth: false, routerOptions: { initialEntries: ['/activity'] } });
    fireEvent.click(screen.getByRole('link', { name: 'Details' }));
    const location = JSON.parse(screen.getByTestId('location').textContent!);
    expect(location.state.titleReturn).toEqual({ href: '/', label: 'Home', section: 'home' });
  });

  it('carries named collection context through title and play links', () => {
    render(<CollectionNavigationName value="Weekend favorites"><TitleLink to="/titles/game">Details</TitleLink><TitleLink to="/titles/game/releases/version/play">Play</TitleLink><LocationProbe /></CollectionNavigationName>, {
      withAuth: false, routerOptions: { initialEntries: ['/collections/favorites'] },
    });
    fireEvent.click(screen.getByRole('link', { name: 'Details' }));
    let location = JSON.parse(screen.getByTestId('location').textContent!);
    expect(location.state.titleReturn).toMatchObject({ href: '/collections/favorites', label: 'Weekend favorites', section: 'home' });
    const original = location.state.titleReturn;
    fireEvent.click(screen.getByRole('link', { name: 'Play' }));
    location = JSON.parse(screen.getByTestId('location').textContent!);
    expect(location.path).toBe('/titles/game/releases/version/play');
    expect(location.state.titleReturn).toEqual(original);
  });
});
