import type {
  AccountLink,
  AccountSecurity,
  AccountSession,
  CollectionDetail,
  LibraryDiscSetOption,
  LibraryDiscSetOptions,
  LibraryEvaluation,
  LibraryEvaluationTitle,
} from '@romd/admin-api-client';
import type { CollectionDetailView } from '../hooks/api/useCollectionManagement';

export function finalPage<T>(
  value:
    | T[]
    | {
        items: T[];
        nextCursor?: string | null;
        hasNextPage?: boolean;
      },
) {
  if (Array.isArray(value))
    return {
      items: value,
      nextCursor: null,
      hasNextPage: false,
    };
  return {
    ...value,
    nextCursor: value.nextCursor ?? null,
    hasNextPage: value.hasNextPage ?? value.nextCursor != null,
  };
}

export function collectionPage(value: CollectionDetailView | CollectionDetail): CollectionDetail {
  return {
    ...value,
    items: finalPage(value.items),
  };
}

export function evaluationPage(
  value:
    | LibraryEvaluation
    | (Omit<LibraryEvaluation, 'page'> & {
        items: LibraryEvaluationTitle[];
        nextCursor?: string | null;
        hasNextPage?: boolean;
      }),
): LibraryEvaluation {
  if ('page' in value) return value;
  const { items, nextCursor, hasNextPage, ...summary } = value;
  return {
    ...summary,
    page: finalPage({
      items,
      nextCursor,
      hasNextPage,
    }),
  };
}

export function securityPages(
  value:
    | AccountSecurity
    | {
        links: AccountLink[];
        sessions: AccountSession[];
      },
): AccountSecurity {
  return {
    links: finalPage(value.links),
    sessions: finalPage(value.sessions),
  };
}

export function discSetPage(value: {
  selectedDiscSetIds: string[];
  selectedItems: LibraryDiscSetOption[];
  items: LibraryDiscSetOption[];
  nextAfter: string | null;
}): LibraryDiscSetOptions {
  return {
    selectedIds: value.selectedDiscSetIds,
    selected: value.selectedItems,
    page: finalPage({
      items: value.items,
      nextCursor: value.nextAfter,
    }),
  };
}
