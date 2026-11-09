import type { DatCatalogSubscription } from '@romd/admin-api-client';

export function systemAttention(systemKey: string, subscriptions: DatCatalogSubscription[]) {
  const rows = subscriptions.filter((s) => s.systemKey === systemKey);
  return {
    updates: rows.filter((s) => s.state === 'UpdateAvailable' || s.state === 'ReadyToImport')
      .length,
    failed: rows.filter((s) => s.state === 'CheckFailed').length,
  };
}
