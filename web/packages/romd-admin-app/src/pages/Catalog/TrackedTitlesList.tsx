import { Badge, Box, Group, Skeleton, Stack, Text } from '@mantine/core';
import type { TrackedCollectionTitleDto } from '@romd/admin-api-client';
import { IconCircleArrowUp, IconCircleDashed } from '@tabler/icons-react';
import { Link } from 'react-router';
import { useMissingTrackedTitles, useTrackedTitleUpgrades } from '../../hooks/api/useTrackedCollection';
import { usePermissions } from '../../hooks/usePermissions';
import { CatalogTrackingAction } from './CatalogTrackingAction';

type TrackedCatalogView = 'missing' | 'upgrades';

interface TrackedTitleRowProps {
  title: TrackedCollectionTitleDto;
  view: TrackedCatalogView;
}

function TrackedTitleRow({ title, view }: TrackedTitleRowProps) {
  const { canManageTitles } = usePermissions();
  const isUpgrade = view === 'upgrades';

  return (
    <Box py="md" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      <Group justify="space-between" gap="md" wrap="nowrap">
        <Stack gap={3} style={{ minWidth: 0 }}>
          <Text
            component={Link}
            to={`/titles/${title.titleId}`}
            fw={600}
            truncate
            style={{ color: 'inherit', textDecoration: 'none' }}
          >
            {title.titleName}
          </Text>
          <Text size="sm" c="dimmed">
            {title.platformName}
          </Text>
          {isUpgrade && title.desiredRelease && (
            <Text size="xs" c="dimmed">
              Preferred: {title.desiredRelease.name}
              {title.ownedRelease ? ` · Owned: ${title.ownedRelease.name}` : ''}
            </Text>
          )}
        </Stack>

        <Group gap="xs" wrap="nowrap">
          <Badge
            color={isUpgrade ? 'violet' : 'orange'}
            variant="light"
            leftSection={
              isUpgrade ? <IconCircleArrowUp size={12} /> : <IconCircleDashed size={12} />
            }
          >
            {isUpgrade ? 'Preferred release missing' : 'No complete release'}
          </Badge>
          {canManageTitles && <CatalogTrackingAction title={{ id: title.titleId, name: title.titleName, systemKey: title.systemKey, isTracked: true }} />}
        </Group>
      </Group>
    </Box>
  );
}

type TrackedTitlesQuery = ReturnType<typeof useMissingTrackedTitles>;

function TrackedTitlesList({ view, query }: { view: TrackedCatalogView; query: TrackedTitlesQuery }) {
  const label = view === 'missing' ? 'missing tracked titles' : 'tracked title upgrades';

  if (query.isLoading) {
    return (
      <Stack gap="sm" data-testid={`catalog-${view}-loading`}>
        {[1, 2, 3].map((row) => (
          <Skeleton key={row} height={88} radius="md" />
        ))}
      </Stack>
    );
  }

  if (query.isError) {
    return (
      <Text c="red" size="sm" role="alert">
        Failed to load {label}. Please try again.
      </Text>
    );
  }

  if (!query.data || query.data.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        {view === 'missing' ? 'Every tracked title has a complete release.' : 'No preferred releases are missing.'}
      </Text>
    );
  }

  return (
    <Stack gap="sm" data-testid={`catalog-${view}-list`}>
      {query.data.map((title) => (
        <TrackedTitleRow key={title.titleId} title={title} view={view} />
      ))}
    </Stack>
  );
}

export function MissingTrackedTitlesList() {
  return <TrackedTitlesList view="missing" query={useMissingTrackedTitles()} />;
}

export function TrackedTitleUpgradesList() {
  return <TrackedTitlesList view="upgrades" query={useTrackedTitleUpgrades()} />;
}
