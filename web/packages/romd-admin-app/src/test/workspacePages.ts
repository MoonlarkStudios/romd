import type {
  AccountLinkDto,
  AccountSecurityDto,
  AccountSessionDto,
  CollectionDetail,
  LibraryDiscSetOptionDto,
  LibraryDiscSetOptionsDto,
  LibraryEvaluationDto,
  LibraryEvaluationTitleDto,
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
    | LibraryEvaluationDto
    | (Omit<LibraryEvaluationDto, 'page'> & {
        items: LibraryEvaluationTitleDto[];
        nextCursor?: string | null;
        hasNextPage?: boolean;
      }),
): LibraryEvaluationDto {
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
    | AccountSecurityDto
    | {
        links: AccountLinkDto[];
        sessions: AccountSessionDto[];
      },
): AccountSecurityDto {
  return {
    links: finalPage(value.links),
    sessions: finalPage(value.sessions),
  };
}

export function discSetPage(value: {
  selectedDiscSetIds: string[];
  selectedItems: LibraryDiscSetOptionDto[];
  items: LibraryDiscSetOptionDto[];
  nextAfter: string | null;
}): LibraryDiscSetOptionsDto {
  return {
    selectedIds: value.selectedDiscSetIds,
    selected: value.selectedItems,
    page: finalPage({
      items: value.items,
      nextCursor: value.nextAfter,
    }),
  };
}
