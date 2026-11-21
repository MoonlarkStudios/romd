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
  const unidentified = health?.unidentifiedCount ?? 0;
  const unrouted = health?.unroutedCount ?? 0;
  const status = isLoading
    ? 'loading'
    : isError && health
      ? 'stale'
      : unavailable
        ? 'unavailable'
        : unidentified > 0 || unrouted > 0
          ? 'needs attention'
          : 'identified & routed';
  const observed = !isLoading && !isError && !unavailable;
  const color = !observed ? 'gray' : status === 'needs attention' ? 'orange' : 'green';
  const unidentifiedLabel = `${unidentified.toLocaleString()} unidentified ROM${unidentified === 1 ? '' : 's'}`;
  const unroutedLabel = `${unrouted.toLocaleString()} unrouted ROM${unrouted === 1 ? '' : 's'}`;
  const label =
    status !== 'needs attention'
      ? status
      : unidentified === 0
        ? unroutedLabel
        : unrouted === 0
          ? unidentifiedLabel
          : `${(unidentified + unrouted).toLocaleString()} ROMs need attention`;
  const details = observed
    ? `${unidentifiedLabel}. ${unroutedLabel}.`
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
        aria-label={`Library: ${label}`}
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
            color={`var(--mantine-color-${color}-light-color)`}
          />
          <Text
            size="xs"
            c="dimmed"
          >
            Library: {label}
          </Text>
        </Group>
      </UnstyledButton>
    </Tooltip>
  );
}
