import type { Dat, DatCatalogDirectory, DatCatalogSubscription } from '@romd/admin-api-client';

export function connectableCatalog(
  directory: DatCatalogDirectory | undefined,
  subscriptions: DatCatalogSubscription[] | undefined,
  systemKey: string,
  dat: Dat,
) {
  if (
    !directory?.enabled ||
    directory.message ||
    !subscriptions ||
    dat.sourceStatus !== 'Active' ||
    dat.hasSubscription
  ) {
    return undefined;
  }

  return directory.catalogs.find(
    (catalog) =>
      catalog.systemId === systemKey &&
      catalog.name === dat.name &&
      !subscriptions.some(
        (subscription) =>
          subscription.activeDatId === dat.id ||
          (subscription.catalogId === catalog.catalogId && !subscription.activeDatId),
      ),
  );
}

export function datProviderLabel(type: string) {
  switch (type) {
    case 'NoIntro':
      return 'No-Intro';
    case 'Redump':
      return 'Redump';
    case 'Unknown':
      return undefined;
    default:
      return type;
  }
}
