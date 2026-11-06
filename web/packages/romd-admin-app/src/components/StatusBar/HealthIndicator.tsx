import { Group, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconCircleFilled } from '@tabler/icons-react';
import { Link } from 'react-router';
import type { HealthStats } from '../../hooks/api/useHealthStats';

interface HealthIndicatorProps {
  health: HealthStats | undefined;
  isLoading: boolean;
  isError: boolean;
}

export function HealthIndicator({ health, isLoading, isError }: HealthIndicatorProps) {
  const unavailable = !health;
  const status = isLoading
    ? 'loading'
    : isError && health
      ? 'stale'
      : unavailable
        ? 'unavailable'
        : health.unidentifiedCount > 0 || health.unroutedCount > 0
          ? 'needs attention'
          : 'identified & routed';
  const observed = !isLoading && !isError && !unavailable;
  const color = !observed ? 'gray' : status === 'needs attention' ? 'orange' : 'green';
  const details = observed
    ? `${health.unidentifiedCount} unidentified ROMs; ${health.unroutedCount} unrouted ROMs. Library identification and routing only.`
    : isLoading
      ? 'Loading library identification and routing counts.'
      : status === 'stale'
        ? 'Library counts could not be refreshed. The previous counts are stale.'
        : 'Library identification and routing counts are unavailable.';

  return (
    <Tooltip label={details}>
      <UnstyledButton
        component={Link}
        to={observed && status === 'needs attention' ? '/roms?tab=attention' : '/roms?tab=all'}
        aria-label={`Library: ${status}`}
        px="sm"
        h="100%"
        style={{
          display: 'flex',
          alignItems: 'center',
          borderRadius: 0,
        }}
      >
        <Group
          gap={6}
          wrap="nowrap"
        >
          <IconCircleFilled
            size={8}
            color={`var(--mantine-color-${color}-5)`}
          />
          <Text
            size="xs"
            c="dimmed"
          >
            Library: {status}
          </Text>
        </Group>
      </UnstyledButton>
    </Tooltip>
  );
}
