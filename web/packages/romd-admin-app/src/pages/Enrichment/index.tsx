import { Badge, Group, Stack, Tabs, Text, ThemeIcon, Title } from '@mantine/core';
import {
  IconCheck,
  IconClock,
  IconSearch,
  IconSparkles,
  IconX,
} from '@tabler/icons-react';
import { useSystemStats } from '../../hooks/api/useSystemStats';
import { CompletedTitlesList } from './CompletedTitlesList';
import { EnrichmentQueue } from './EnrichmentQueue';
import { FailedTitlesList } from './FailedTitlesList';
import { NotFoundTitlesList } from './NotFoundTitlesList';

/**
 * Enrichment management page for Manager+ users.
 * Provides visibility into enrichment queue and allows management of failed/not-found titles.
 */
export function Enrichment() {
  const { data: stats } = useSystemStats({ pollingInterval: 10000 });

  const enrichment = stats?.enrichment;
  const pendingCount = Number(enrichment?.pending ?? 0);
  const failedCount = Number(enrichment?.failed ?? 0);
  const notFoundCount = Number(enrichment?.notFound ?? 0);
  const completedCount = Number(enrichment?.completed ?? 0);

  return (
    <Stack gap="lg">
      <Group gap="md">
        <ThemeIcon size="lg" variant="light" color="violet">
          <IconSparkles size={20} />
        </ThemeIcon>
        <div>
          <Title order={2}>Enrichment Management</Title>
          <Text size="sm" c="dimmed">
            Monitor and manage metadata enrichment tasks
          </Text>
        </div>
      </Group>

      <Tabs defaultValue="queue">
        <Tabs.List>
          <Tabs.Tab value="queue" leftSection={<IconClock size={16} />}>
            Queue
            {pendingCount > 0 && (
              <Badge size="sm" variant="light" color="blue" ml="xs">
                {pendingCount}
              </Badge>
            )}
          </Tabs.Tab>
          <Tabs.Tab value="failed" leftSection={<IconX size={16} />}>
            Failed
            {failedCount > 0 && (
              <Badge size="sm" variant="light" color="red" ml="xs">
                {failedCount}
              </Badge>
            )}
          </Tabs.Tab>
          <Tabs.Tab value="not-found" leftSection={<IconSearch size={16} />}>
            Not Found
            {notFoundCount > 0 && (
              <Badge size="sm" variant="light" color="orange" ml="xs">
                {notFoundCount}
              </Badge>
            )}
          </Tabs.Tab>
          <Tabs.Tab value="completed" leftSection={<IconCheck size={16} />}>
            Completed
            {completedCount > 0 && (
              <Badge size="sm" variant="light" color="green" ml="xs">
                {completedCount}
              </Badge>
            )}
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="queue" pt="md">
          <EnrichmentQueue />
        </Tabs.Panel>

        <Tabs.Panel value="failed" pt="md">
          <FailedTitlesList />
        </Tabs.Panel>

        <Tabs.Panel value="not-found" pt="md">
          <NotFoundTitlesList />
        </Tabs.Panel>

        <Tabs.Panel value="completed" pt="md">
          <CompletedTitlesList />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
