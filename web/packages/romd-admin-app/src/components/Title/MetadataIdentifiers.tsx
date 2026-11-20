import { Anchor, Badge, Code, Group, Stack, Text, Title } from '@mantine/core';
import type { ProviderExternalId } from '@romd/admin-api-client';
import { IconExternalLink } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { providerMatchesQuery } from '../../hooks/api/useProviderMatches';
import { SourceReferencesPanel } from '../../pages/TitleDetail/SourceReferencesPanel';
import workspace from '../Workspace/Workspace.module.css';
import layout from './MetadataPanel.module.css';
import { sourceLabel } from './metadataDraft';

export function MetadataIdentifiers({
  titleId,
  externalIds,
}: {
  titleId: string;
  externalIds?: ProviderExternalId[];
}) {
  const matches = useQuery({
    ...providerMatchesQuery(titleId),
    enabled: !!externalIds?.length,
  });

  return (
    <Stack
      component="section"
      className={workspace.panel}
      gap="md"
    >
      <Title
        order={2}
        className={workspace.sectionHeading}
      >
        Identifiers and sources
      </Title>
      <dl className={layout.identifiers}>
        <div>
          <dt className="romd-metadata">ROMD</dt>
          <dd>
            <Code>{titleId}</Code>
          </dd>
        </div>
        {externalIds?.map((entry) => {
          const match = matches.data?.find(
            (row) =>
              row.providerId.toLowerCase() === entry.provider.toLowerCase() &&
              row.game?.id === entry.externalId,
          );
          const url = match?.game?.url;
          const provider = match?.providerName ?? sourceLabel(entry.provider);
          return (
            <div key={`${entry.provider}:${entry.externalId}`}>
              <dt className="romd-metadata">{provider}</dt>
              <dd>
                <Group gap="xs">
                  {url?.startsWith('https://') ? (
                    <Anchor
                      href={url}
                      target="_blank"
                      rel="noopener noreferrer"
                      size="sm"
                      aria-label={`Open ${provider} ID ${entry.externalId} (opens in a new tab)`}
                    >
                      <Group
                        gap={4}
                        wrap="nowrap"
                      >
                        {entry.externalId}
                        <IconExternalLink
                          size={14}
                          aria-hidden
                        />
                      </Group>
                    </Anchor>
                  ) : (
                    <Code>{entry.externalId}</Code>
                  )}
                  <Badge
                    variant="default"
                    size="sm"
                  >
                    {entry.isConfirmed ? 'Confirmed' : 'Unconfirmed'}
                  </Badge>
                </Group>
              </dd>
            </div>
          );
        })}
      </dl>
      {externalIds?.length === 0 && (
        <Text
          size="sm"
          c="dimmed"
        >
          No linked provider IDs.
        </Text>
      )}
      <Title
        order={3}
        className="romd-card-title"
      >
        Catalog sources
      </Title>
      <SourceReferencesPanel titleId={titleId} />
    </Stack>
  );
}
