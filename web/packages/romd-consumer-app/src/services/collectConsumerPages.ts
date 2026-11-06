interface ConsumerPage<T> {
  items: T[];
  nextCursor?: string | null;
  hasNextPage: boolean;
}

export async function collectConsumerPages<
  T extends {
    id: string;
  },
>(
  load: (cursor: string | undefined) => Promise<ConsumerPage<T>>,
  firstPage?: ConsumerPage<T>,
): Promise<T[]> {
  const items: T[] = [];
  const ids = new Set<string>();
  const cursors = new Set<string>();
  let cursor: string | undefined;
  let page = firstPage;

  for (;;) {
    page ??= await load(cursor);
    if (
      !page ||
      typeof page !== 'object' ||
      Object.keys(page).length !== 3 ||
      !Object.hasOwn(page, 'nextCursor') ||
      !Array.isArray(page.items) ||
      page.items.length > 100 ||
      typeof page.hasNextPage !== 'boolean'
    ) {
      throw new Error('The server returned an invalid page.');
    }

    const next = page.nextCursor;
    if (
      (page.hasNextPage && (typeof next !== 'string' || next.length === 0 || next.length > 1024)) ||
      (!page.hasNextPage && next !== null)
    ) {
      throw new Error('The server returned an invalid page continuation.');
    }

    for (const item of page.items) {
      if (!item || typeof item.id !== 'string' || !item.id || ids.has(item.id)) {
        throw new Error('The server returned an invalid or repeated page item.');
      }
      ids.add(item.id);
      items.push(item);
    }
    if (!page.hasNextPage) return items;
    if (typeof next !== 'string' || cursors.has(next)) {
      throw new Error('The server repeated a page continuation.');
    }
    cursors.add(next);
    cursor = next;
    page = undefined;
  }
}
