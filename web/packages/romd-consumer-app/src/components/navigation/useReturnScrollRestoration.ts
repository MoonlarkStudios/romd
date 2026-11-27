import { useLayoutEffect, useRef } from 'react';
import { useLocation, useNavigationType } from 'react-router';
import { type ReturnContext, readReturnContext } from './titleNavigation';

/** Wait briefly for cached/asynchronous shelves to mount; user scrolling always wins. */
export function useReturnScrollRestoration() {
  const location = useLocation();
  const navigationType = useNavigationType();
  const previousPath = useRef<string | null>(null);
  const consolePositions = useRef(new Map<string, number>());
  const consoleListKey = useRef<string | undefined>(undefined);
  useLayoutEffect(() => {
    const changedPage = previousPath.current !== location.pathname;
    previousPath.current = location.pathname;
    const isConsoleList = location.pathname === '/systems';
    const isConsolePage = isConsoleList || location.pathname.startsWith('/systems/');
    const positions = consolePositions.current;
    const savedKey =
      isConsoleList && location.state?.restoreConsoleList === true
        ? consoleListKey.current
        : isConsolePage && navigationType === 'POP'
          ? location.key
          : undefined;
    const consolePosition = savedKey === undefined ? undefined : positions.get(savedKey);
    const context = readReturnContext({
      titleReturn: location.state?.restoreTitleReturn,
    });
    const scroll: ReturnContext['scroll'] =
      consolePosition !== undefined
        ? {
            y: consolePosition,
            rails: {},
          }
        : location.state?.restoreTitleReturn &&
            context.href === `${location.pathname}${location.search}${location.hash}`
          ? context.scroll
          : undefined;
    if (!scroll && changedPage && (isConsolePage || location.pathname.startsWith('/titles/'))) {
      window.scrollTo(0, 0);
    }
    const rememberConsolePosition = () => {
      positions.set(location.key, window.scrollY);
    };
    const stopRemembering = () => {
      window.removeEventListener('scroll', rememberConsolePosition);
      window.removeEventListener('click', rememberConsolePosition, true);
    };
    if (isConsoleList) {
      consoleListKey.current = location.key;
    }
    if (isConsolePage) {
      positions.delete(location.key);
      rememberConsolePosition();
      // Keep recent browser entries in this session, including the latest console list.
      if (positions.size > 32) {
        const oldest = [
          ...positions.keys(),
        ].find((key) => key !== consoleListKey.current);
        if (oldest !== undefined) positions.delete(oldest);
      }
      window.addEventListener('scroll', rememberConsolePosition, {
        passive: true,
      });
      // Read the departing position before a link can replace the page or the scroll event arrives.
      window.addEventListener('click', rememberConsolePosition, true);
    }
    if (
      !scroll ||
      !Number.isFinite(scroll.y) ||
      scroll.y < 0 ||
      !scroll.rails ||
      typeof scroll.rails !== 'object'
    )
      return stopRemembering;
    let timer: ReturnType<typeof setTimeout>;
    let stopped = false;
    const deadline = Date.now() + 5000;
    const stop = () => {
      stopped = true;
      clearTimeout(timer);
    };
    const restore = () => {
      if (stopped) return;
      const found = new Set<string>();
      for (const rail of document.querySelectorAll<HTMLElement>('[data-navigation-rail]')) {
        const key = rail.dataset.navigationRail ?? '';
        const left = scroll.rails[key];
        if (Number.isFinite(left) && left >= 0) {
          rail.scrollLeft = left;
          if (Math.abs(rail.scrollLeft - left) < 2) found.add(key);
        }
      }
      window.scrollTo(0, scroll.y);
      if (
        (Math.abs(window.scrollY - scroll.y) < 2 &&
          Object.keys(scroll.rails).every((key) => found.has(key))) ||
        Date.now() >= deadline
      )
        return;
      timer = setTimeout(restore, 50);
    };
    if (consolePosition !== undefined && navigationType === 'POP') {
      // Let the browser finish its history traversal before restoring the SPA position.
      timer = setTimeout(restore, 0);
    } else {
      restore();
    }
    window.addEventListener('wheel', stop, {
      passive: true,
    });
    window.addEventListener('touchstart', stop, {
      passive: true,
    });
    window.addEventListener('keydown', stop);
    return () => {
      stop();
      stopRemembering();
      window.removeEventListener('wheel', stop);
      window.removeEventListener('touchstart', stop);
      window.removeEventListener('keydown', stop);
    };
  }, [
    location,
    navigationType,
  ]);
}
