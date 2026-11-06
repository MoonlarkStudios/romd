export interface CursorPage<T> {
  items: readonly T[];
  nextCursor?: string | null;
  hasNextPage: boolean;
}

/** Completes a list before callers make a decision that depends on its whole scope. */
export async function collectPages<T>(
  loadPage: (cursor: string | undefined) => Promise<CursorPage<T>>,
  firstPage?: CursorPage<T>,
): Promise<T[]> {
  const items: T[] = [];
  const cursors = new Set<string>();
  let page = firstPage === undefined ? await loadPage(undefined) : firstPage;

  for (;;) {
    if (
      !page ||
      !Array.isArray(page.items) ||
      typeof page.hasNextPage !== 'boolean' ||
      !Object.hasOwn(page, 'nextCursor') ||
      (page.nextCursor !== null &&
        (typeof page.nextCursor !== 'string' || page.nextCursor.trim().length === 0)) ||
      page.hasNextPage !== (typeof page.nextCursor === 'string')
    ) {
      throw new Error('The server returned an invalid page.');
    }

    items.push(...page.items);
    if (!page.hasNextPage) {
      return items;
    }

    const nextCursor = page.nextCursor;
    if (typeof nextCursor !== 'string' || cursors.has(nextCursor)) {
      throw new Error('The server repeated a page cursor.');
    }
    cursors.add(nextCursor);
    page = await loadPage(nextCursor);
  }
}
