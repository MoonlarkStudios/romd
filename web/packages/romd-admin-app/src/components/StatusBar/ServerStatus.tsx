import { Group, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconCircleFilled } from '@tabler/icons-react';
import { Link } from 'react-router';
import { useServerReadiness } from '../../hooks/api/useServerReadiness';

export function ServerStatus() {
  const query = useServerReadiness();
  const unavailable = query.isError || !query.data;
  const stale = Boolean(query.data) && (query.isError || query.isStale);
  const status = query.isPending
    ? 'checking'
    : stale
      ? 'stale'
      : unavailable
        ? 'unavailable'
        : query.data?.status;
  const color =
    status === 'ready'
      ? 'green'
      : status === 'unready'
        ? 'red'
        : status === 'degraded'
          ? 'orange'
          : 'gray';
  const details = stale
    ? 'The last readiness result is stale. Open Diagnostics to investigate.'
    : unavailable
      ? 'Server readiness could not be established. Open Diagnostics to investigate.'
      : query.data?.checks
          .filter((check) => check.status !== 'healthy')
          .map((check) => `${check.name}: ${check.status}`)
          .join('; ') || 'Server readiness checks passed.';

  return (
    <Tooltip label={query.isPending ? 'Checking server readiness.' : details}>
      <UnstyledButton
        component={Link}
        to="/diagnostics"
        px="sm"
        h="100%"
        aria-label={`Server: ${status}. Open diagnostics`}
        style={{
          display: 'flex',
          alignItems: 'center',
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
            Server: {status}
          </Text>
        </Group>
      </UnstyledButton>
    </Tooltip>
  );
}
