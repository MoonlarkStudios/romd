import type { CatalogTitle, SystemResource } from '@romd/admin-api-client';

export function catalogGameLabel(
  title: CatalogTitle,
  systems: readonly SystemResource[] | undefined,
) {
  const system = systems?.find(({ key }) => key === title.systemKey)?.name ?? title.systemKey;
  const availability =
    title.hasLocalPayload === true
      ? 'Local files'
      : title.hasLocalPayload === false
        ? 'No local files'
        : 'File status unknown';
  return {
    system,
    availability,
  };
}
