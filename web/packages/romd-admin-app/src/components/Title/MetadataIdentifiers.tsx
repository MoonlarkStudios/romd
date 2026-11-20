import { Code, Group, Stack, Text, Title } from '@mantine/core';
import type { ProviderExternalId } from '@romd/admin-api-client';
import { usePermissions } from '../../hooks/usePermissions';
import { SourceReferencesPanel } from '../../pages/TitleDetail/SourceReferencesPanel';
import { useDiscardEpoch } from '../Administration/DraftGuard';
import workspace from '../Workspace/Workspace.module.css';
import layout from './MetadataPanel.module.css';
import { ProviderMatches } from './ProviderMatches';
import { titleSourcesAnchor } from './titleSections';

export function MetadataIdentifiers({
  titleId,
  titleName,
  externalIds,
  initialProviderId,
  providerRequestKey,
  metadataDraftPending,
}: {
  titleId: string;
  titleName: string;
  externalIds?: ProviderExternalId[];
  initialProviderId?: string;
  providerRequestKey?: string;
  metadataDraftPending: boolean;
}) {
  const { canManageTitles } = usePermissions();
  const epoch = useDiscardEpoch();

  return (
    <Stack
      component="section"
      className={`${workspace.panel} ${layout.sources}`}
      id={titleSourcesAnchor}
      aria-labelledby="title-sources-heading"
      tabIndex={-1}
      gap="sm"
    >
      <Group justify="space-between">
        <Title
          id="title-sources-heading"
          order={2}
          className={workspace.sectionHeading}
        >
          Identifiers and sources
        </Title>
        <dl className={layout.titleIdentifier}>
          <dt className="romd-metadata">ROMD ID</dt>
          <dd>
            <Code>{titleId}</Code>
          </dd>
        </dl>
      </Group>
      {canManageTitles && (
        <ProviderMatches
          key={epoch}
          titleId={titleId}
          titleName={titleName}
          externalIds={externalIds}
          initialProviderId={initialProviderId}
          providerRequestKey={providerRequestKey}
          density="compact"
          disabled={metadataDraftPending}
        />
      )}
      {metadataDraftPending && (
        <Text
          size="sm"
          c="dimmed"
          role="status"
        >
          Save or discard your metadata draft below to change external sources.
        </Text>
      )}
      <details className={layout.catalogSources}>
        <summary>Catalog sources</summary>
        <div className={layout.catalogSourceList}>
          <SourceReferencesPanel titleId={titleId} />
        </div>
      </details>
    </Stack>
  );
}
