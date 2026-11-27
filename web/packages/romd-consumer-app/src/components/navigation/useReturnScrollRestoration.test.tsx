import { act, fireEvent, screen } from '@testing-library/react';
import { Link, useNavigate } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { useReturnScrollRestoration } from './useReturnScrollRestoration';

function Restoration() {
  useReturnScrollRestoration();
  return null;
}
beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

it.each([
  [
    'Consoles link',
    '/systems',
  ],
  [
    'Browser back',
    '/systems',
  ],
  [
    'Browser back',
    '/systems/genesis',
  ],
])('restores scroll through %s to %s', (returnAction, source) => {
  function ConsoleNavigation() {
    useReturnScrollRestoration();
    const navigate = useNavigate();
    return (
      <>
        <Link to="/systems/snes">Open console</Link>
        <Link
          to="/systems"
          state={{
            restoreConsoleList: true,
          }}
        >
          Consoles link
        </Link>
        <button
          type="button"
          onClick={() => void navigate(-1)}
        >
          Browser back
        </button>
      </>
    );
  }

  let y = 0;
  vi.spyOn(window, 'scrollY', 'get').mockImplementation(() => y);
  vi.spyOn(window, 'scrollTo').mockImplementation((_x, top) => {
    y = Number(top);
  });
  render(<ConsoleNavigation />, {
    withAuth: false,
    routerOptions: {
      initialEntries: [
        source,
      ],
    },
  });

  y = 280;
  fireEvent.click(screen.getByText('Open console'));
  expect(y).toBe(0);
  fireEvent.click(screen.getByText(returnAction));
  act(() => vi.runOnlyPendingTimers());
  expect(y).toBe(280);
});

it('restores page and rail positions after asynchronous content mounts', () => {
  let y = 0;
  vi.spyOn(window, 'scrollY', 'get').mockImplementation(() => y);
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {
    y = 350;
  });
  const result = render(<Restoration />, {
    withAuth: false,
    routerOptions: {
      initialEntries: [
        {
          pathname: '/',
          state: {
            restoreTitleReturn: {
              href: '/',
              label: 'Home',
              section: 'home',
              scroll: {
                y: 350,
                rails: {
                  favorites: 220,
                },
              },
            },
          },
        },
      ],
    },
  });
  const rail = document.createElement('div');
  rail.dataset.navigationRail = 'favorites';
  document.body.append(rail);
  act(() => vi.advanceTimersByTime(50));
  expect(rail.scrollLeft).toBe(220);
  expect(y).toBe(350);
  result.unmount();
  rail.remove();
});

it('stops retrying restoration when the user scrolls', () => {
  vi.spyOn(window, 'scrollY', 'get').mockReturnValue(0);
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  const result = render(<Restoration />, {
    withAuth: false,
    routerOptions: {
      initialEntries: [
        {
          pathname: '/library',
          state: {
            restoreTitleReturn: {
              href: '/library',
              label: 'Browse',
              section: 'browse',
              scroll: {
                y: 350,
                rails: {},
              },
            },
          },
        },
      ],
    },
  });
  act(() => window.dispatchEvent(new Event('wheel')));
  act(() => vi.advanceTimersByTime(500));
  expect(scroll).toHaveBeenCalledTimes(1);
  result.unmount();
});
