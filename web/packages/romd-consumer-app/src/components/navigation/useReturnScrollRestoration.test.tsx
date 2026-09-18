import { act } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { useReturnScrollRestoration } from './useReturnScrollRestoration';

function Restoration() { useReturnScrollRestoration(); return null; }
beforeEach(() => vi.useFakeTimers());
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

it('restores page and rail positions after asynchronous content mounts', () => {
  let y = 0;
  vi.spyOn(window, 'scrollY', 'get').mockImplementation(() => y);
  vi.spyOn(window, 'scrollTo').mockImplementation(() => { y = 350; });
  const result = render(<Restoration />, { withAuth: false, routerOptions: { initialEntries: [{ pathname: '/', state: { restoreTitleReturn: { href: '/', label: 'Home', section: 'home', scroll: { y: 350, rails: { favorites: 220 } } } } }] } });
  const rail = document.createElement('div');
  rail.dataset.navigationRail = 'favorites';
  document.body.append(rail);
  act(() => vi.advanceTimersByTime(50));
  expect(rail.scrollLeft).toBe(220);
  expect(y).toBe(350);
  result.unmount(); rail.remove();
});

it('stops retrying restoration when the user scrolls', () => {
  vi.spyOn(window, 'scrollY', 'get').mockReturnValue(0);
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  const result = render(<Restoration />, { withAuth: false, routerOptions: { initialEntries: [{ pathname: '/library', state: { restoreTitleReturn: { href: '/library', label: 'Browse', section: 'browse', scroll: { y: 350, rails: {} } } } }] } });
  act(() => window.dispatchEvent(new Event('wheel')));
  act(() => vi.advanceTimersByTime(500));
  expect(scroll).toHaveBeenCalledTimes(1);
  result.unmount();
});
