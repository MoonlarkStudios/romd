import { ActionIcon, Badge, Card, Group, Skeleton, Stack, Text, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconRefresh, IconX } from '@tabler/icons-react';
import { Link } from 'react-router';
import { useTriggerEnrichment } from '../../hooks/api/useTitleActions';
import { useTitlesByEnrichmentStatus } from './useTitlesByEnrichmentStatus';

interface TitleListItemProps {
  title: CatalogTitle;
}

function TitleListItem({ title }: TitleListItemProps) {
  const triggerEnrichment = useTriggerEnrichment();

  const handleRetry = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    try {
      await triggerEnrichment.mutateAsync(title.id);
      notifications.show({
        title: 'Enrichment restarted',
        message: `Retrying enrichment for "${title.name}"`,
        color: 'blue',
      });
    } catch {
      notifications.show({
        title: 'Retry failed',
        message: 'Could not restart enrichment',
        color: 'red',
      });
    }
  };

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
        <Group gap="xs">
          <Badge variant="light" color="red" leftSection={<IconX size={12} />}>
            Failed
          </Badge>
          <Tooltip label="Retry enrichment">
            <ActionIcon
              variant="light"
              color="blue"
              size="sm"
              onClick={handleRetry}
              loading={triggerEnrichment.isPending}
            >
              <IconRefresh size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
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
 * Shows titles that failed enrichment with retry option.
 */
export function FailedTitlesList() {
  const { data: titles, isLoading } = useTitlesByEnrichmentStatus('Failed');

  if (isLoading) {
    return <LoadingState />;
  }

  if (!titles || titles.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="xl">
        No titles with failed enrichment.
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
