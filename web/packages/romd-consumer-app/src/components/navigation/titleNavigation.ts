import { createContext } from 'react';

export interface ReturnContext {
  href: string;
  label: string;
  section: 'home' | 'browse';
  scroll?: { y: number; rails: Record<string, number> };
}
export const CollectionNavigationName = createContext<string | null>(null);
const fallback: ReturnContext = { href: '/library', label: 'Browse', section: 'browse' };

/** Accept only supported internal destinations, including history restored by the browser. */
export function readReturnContext(state: unknown): ReturnContext {
  const candidate = (state as { titleReturn?: ReturnContext } | null)?.titleReturn;
  if (!candidate || typeof candidate.href !== 'string') return fallback;
  const path = candidate.href.split(/[?#]/)[0];
  const section = path === '/library' ? 'browse' : path === '/' || /^\/collections\/[A-Za-z0-9_-]+$/.test(path) ? 'home' : null;
  if (!section) return fallback;
  return { ...candidate, section, label: path === '/' ? 'Home' : path === '/library' ? 'Browse' : typeof candidate.label === 'string' && candidate.label.trim() ? candidate.label : 'Collection' };
}

export function titleOrigin(location: { pathname: string; search: string; hash: string; state: unknown }, collectionName: string | null): ReturnContext {
  if (location.pathname.startsWith('/titles/')) return readReturnContext(location.state);
  if (location.pathname === '/library') return { href: `/library${location.search}${location.hash}`, label: 'Browse', section: 'browse' };
  if (/^\/collections\/[A-Za-z0-9_-]+$/.test(location.pathname)) return { href: `${location.pathname}${location.search}${location.hash}`, label: collectionName ?? 'Collection', section: 'home' };
  return { href: '/', label: 'Home', section: 'home' };
}

export function captureReturnScroll(): NonNullable<ReturnContext['scroll']> {
  const rails: Record<string, number> = {};
  for (const element of document.querySelectorAll<HTMLElement>('[data-navigation-rail]')) {
    const key = element.dataset.navigationRail;
    if (key) rails[key] = element.scrollLeft;
  }
  return { y: window.scrollY, rails };
}
