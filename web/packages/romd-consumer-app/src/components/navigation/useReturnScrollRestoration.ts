import { useEffect, useRef } from 'react';
import { useLocation } from 'react-router';
import { readReturnContext } from './titleNavigation';

/** Wait briefly for cached/asynchronous shelves to mount; user scrolling always wins. */
export function useReturnScrollRestoration() {
  const location = useLocation();
  const previousPath = useRef<string | null>(null);
  useEffect(() => {
    const changedPage = previousPath.current !== location.pathname;
    previousPath.current = location.pathname;
    const restored = location.state?.restoreTitleReturn;
    if (!restored) {
      if (changedPage && location.pathname.startsWith('/titles/')) window.scrollTo(0, 0);
      return;
    }
    const context = readReturnContext({ titleReturn: restored });
    if (context.href !== `${location.pathname}${location.search}${location.hash}`) return;
    const scroll = context.scroll;
    if (!scroll || !Number.isFinite(scroll.y) || scroll.y < 0 || !scroll.rails || typeof scroll.rails !== 'object') return;
    let timer: ReturnType<typeof setTimeout>;
    let stopped = false;
    const deadline = Date.now() + 5000;
    const stop = () => { stopped = true; clearTimeout(timer); };
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
      if ((Math.abs(window.scrollY - scroll.y) < 2 && Object.keys(scroll.rails).every(key => found.has(key))) || Date.now() >= deadline) return;
      timer = setTimeout(restore, 50);
    };
    restore();
    window.addEventListener('wheel', stop, { passive: true });
    window.addEventListener('touchstart', stop, { passive: true });
    window.addEventListener('keydown', stop);
    return () => {
      stop();
      window.removeEventListener('wheel', stop);
      window.removeEventListener('touchstart', stop);
      window.removeEventListener('keydown', stop);
    };
  }, [location]);
}
