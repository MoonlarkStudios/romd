import { Alert, Text } from '@mantine/core';

/** Refresh health is independent of availability of an authenticated retained document. */
export function CatalogRefreshNotice({
  catalog,
}: {
  catalog: {
    health: string;
    documentHash?: string | null;
    lastChangedAt?: string | null;
  };
}) {
  if (catalog.health === 'healthy') return null;
  return (
    <Alert
      color="yellow"
      title="Latest refresh delayed"
    >
      {catalog.documentHash
        ? 'The latest upstream refresh did not succeed. The last verified catalog is still available. ROMD will verify it again before preparing your review.'
        : 'No verified catalog is available yet. Try again later or upload a DAT.'}
      {catalog.lastChangedAt && (
        <Text size="sm">
          Catalog last changed {new Date(catalog.lastChangedAt).toLocaleString()}.
        </Text>
      )}
    </Alert>
  );
}
