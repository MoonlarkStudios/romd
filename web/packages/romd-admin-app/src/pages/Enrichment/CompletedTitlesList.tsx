import { Badge, Card, Group, Skeleton, Stack, Text } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconCheck } from '@tabler/icons-react';
import { Link } from 'react-router';
import { useTitlesByEnrichmentStatus } from './useTitlesByEnrichmentStatus';

interface TitleListItemProps {
  title: CatalogTitle;
}

function TitleListItem({ title }: TitleListItemProps) {
  return (
    <Card
      component={Link}
      to={`/titles/${title.id}`}
      withBorder
      padding="sm"
      style={{ textDecoration: 'none', color: 'inherit' }}
    >
      <Group justify="space-between" wrap="nowrap">
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" fw={500} truncate>
            {title.name}
          </Text>
          {title.genre && (
            <Text size="xs" c="dimmed">
              {title.genre}
            </Text>
          )}
        </div>
        <Badge variant="light" color="green" leftSection={<IconCheck size={12} />}>
          Enriched
        </Badge>
      </Group>
    </Card>
  );
}

function LoadingState() {
  return (
    <Stack gap="sm">
      {[1, 2, 3, 4, 5].map((i) => (
        <Skeleton key={i} height={60} />
      ))}
    </Stack>
  );
}

/**
 * Shows recently completed enrichment titles.
 */
export function CompletedTitlesList() {
  const { data: titles, isLoading } = useTitlesByEnrichmentStatus('Completed');

  if (isLoading) {
    return <LoadingState />;
  }

  if (!titles || titles.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="xl">
        No enriched titles found.
      </Text>
    );
  }

  return (
    <Stack gap="sm">
      {titles.map((title) => (
        <TitleListItem key={title.id} title={title} />
      ))}
    </Stack>
  );
}
