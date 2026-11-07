import type { Library } from '@romd/admin-api-client';

// The access-invalidating flag remains separate from additive updates. The existing
// timestamps also identify uploads newer than the last published snapshot.
export function needsLibraryRefresh(library: Library): boolean {
  return (
    library.needsMaterialization ||
    !library.lastMaterializedAt ||
    (!!library.updatedAt && Date.parse(library.updatedAt) > Date.parse(library.lastMaterializedAt))
  );
}

// Consumers keep the published snapshot while changes wait; only a never-published library has none.
export function awaitsFirstPublication(library: Library): boolean {
  return library.needsMaterialization && !library.lastMaterializedAt;
}
