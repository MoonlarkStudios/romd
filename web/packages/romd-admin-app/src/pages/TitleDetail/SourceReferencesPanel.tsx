import { Alert, Anchor, Badge, Center, Group, Loader, Paper, Stack, Text } from '@mantine/core';
import type { SourceKind } from '@romd/admin-api-client';
import { Link } from 'react-router';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { SourceStatusBadge } from '../Systems/SourceStatusBadge';

const KIND_COLORS: Record<SourceKind, string> = {
  Dat: 'grape',
  Import: 'blue',
  Manual: 'gray',
};

export interface SourceReferencesPanelProps {
  titleId: string;
}

/** The catalog sources backing a title, including dormant (non-active) ones. */
export function SourceReferencesPanel({ titleId }: SourceReferencesPanelProps) {
  const { data: references, isLoading, isError } = useTitleSourceReferences(titleId);

  if (isLoading) {
    return (
      <Center py={40}>
        <Loader />
      </Center>
    );
  }

  if (isError) {
    return (
      <Text
        c="red"
        size="sm"
      >
        Failed to load the sources backing this title.
      </Text>
    );
  }

  if (!references || references.length === 0) {
    return (
      <Text
        c="dimmed"
        size="sm"
      >
        No active catalog definition. This title and its personal state remain saved.
      </Text>
    );
  }

  return (
    <Stack gap="sm">
      {!references.some((r) => r.hasActiveDefinition) && (
        <Alert
          color="gray"
          title="No active catalog definition"
        >
          This title and its personal state remain saved. Re-enable a source or add a catalog to
          restore its definitions.
        </Alert>
      )}
      <Text
        size="xs"
        c="dimmed"
      >
        Sources linked to this title. Inactive sources preserve their entries but do not contribute
        active catalog definitions.
      </Text>
      {references.map((reference) => (
        <Paper
          key={reference.catalogSourceId}
          radius="md"
          withBorder
          p="sm"
        >
          <Stack gap="xs">
            <Text
              size="sm"
              fw={500}
              style={{
                overflowWrap: 'anywhere',
              }}
            >
              {reference.datId && reference.systemKey ? (
                <Anchor
                  component={Link}
                  to={`/systems/${reference.systemKey}?dat=${reference.datId}&sourceTitle=${titleId}`}
                  size="sm"
                >
                  {reference.name || 'View source entries'}
                </Anchor>
              ) : (
                reference.name || '—'
              )}
            </Text>
            <Group gap="xs">
              <Badge
                size="xs"
                variant="light"
                radius="sm"
                color={KIND_COLORS[reference.kind]}
              >
                {reference.kind}
              </Badge>
              <SourceStatusBadge status={reference.status} />
              <Text size="sm">{Number(reference.entryCount).toLocaleString()} entries</Text>
              {!reference.hasActiveDefinition && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  No active definition from this source
                </Text>
              )}
            </Group>
          </Stack>
        </Paper>
      ))}
    </Stack>
  );
}
