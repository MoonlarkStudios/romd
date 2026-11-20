import { type MouseEvent, useLayoutEffect } from 'react';

let savedPosition:
  | {
      path: string;
      top: number;
    }
  | undefined;

export function rememberCatalogPosition(path: string, event: MouseEvent<HTMLElement>) {
  if (
    event.defaultPrevented ||
    event.button !== 0 ||
    event.altKey ||
    event.ctrlKey ||
    event.metaKey ||
    event.shiftKey
  )
    return false;
  savedPosition = {
    path,
    top: window.scrollY,
  };
  return true;
}

export function useRestoreCatalogPosition(path: string, ready: boolean) {
  useLayoutEffect(() => {
    if (!ready || savedPosition?.path !== path) return;
    const { top } = savedPosition;
    savedPosition = undefined;
    if (window.scrollY !== top)
      window.scrollTo({
        top,
        behavior: 'instant',
      });
  }, [
    path,
    ready,
  ]);
}
