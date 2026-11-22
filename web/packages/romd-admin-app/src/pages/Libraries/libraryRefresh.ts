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

export function publicationStatus(library: Library) {
  if (library.configurationState !== 'Valid') return 'Needs attention';
  if (!library.lastMaterializedAt) return 'Draft';
  if (needsLibraryRefresh(library)) return 'Unpublished changes';
  return null;
}

export function publicationOutcomeCopy(phase: string, outcome?: string | null) {
  if (outcome === 'CatalogRebuilding')
    return 'A system used by this library is rebuilding. Review and publish again when it is ready.';
  if (outcome === 'InputsChanged')
    return 'The library changed during publication. Review the updated changes and publish again.';
  if (outcome === 'InvalidConfiguration')
    return 'Repair the saved rules before publishing. The invalid publication was withdrawn.';
  if (phase === 'Deferred')
    return 'Publication was deferred. Check the library, then review and publish again.';
  if (phase === 'Failed' || phase === 'CompletedWithErrors')
    return 'Publication failed. Open the job for details.';
  if (phase === 'Cancelled') return 'Publication was cancelled.';
  if (outcome === 'AlreadyPublished') return 'The current library is already published.';
  return 'Publication completed.';
}
