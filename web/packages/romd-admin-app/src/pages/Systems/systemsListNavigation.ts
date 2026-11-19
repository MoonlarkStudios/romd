const listPath = '/systems';

/** The inspector can open detail from a filtered view. Direct visits return to Systems. */
export function systemsListReturnPath(state: unknown): string {
  if (typeof state !== 'object' || state === null || !('systemsReturn' in state)) return listPath;
  const path = state.systemsReturn;
  return typeof path === 'string' && (path === listPath || path.startsWith(`${listPath}?`))
    ? path
    : listPath;
}

let listPosition:
  | {
      path: string;
      top: number;
    }
  | undefined;

export function rememberSystemsListScroll(path: string) {
  listPosition = {
    path,
    top: window.scrollY,
  };
}

export function restoreSystemsListScroll(path: string) {
  if (listPosition?.path !== path) return;
  const { top } = listPosition;
  listPosition = undefined;
  if (window.scrollY !== top)
    window.scrollTo({
      top,
      behavior: 'instant',
    });
}
