import { ActionIcon, Badge, Group, Tooltip } from '@mantine/core';
import { IconRefresh } from '@tabler/icons-react';
import { useHealthCheck } from '../../hooks/useHealthCheck';

export function ApiStatus() {
  const { status, data, error, refresh } = useHealthCheck();

  const getBadgeColor = () => {
    switch (status) {
      case 'healthy':
        return 'green';
      case 'error':
        return 'red';
      default:
        return 'gray';
    }
  };

  const getBadgeText = () => {
    switch (status) {
      case 'healthy':
        return `API: ${data?.status}`;
      case 'error':
        return `API: ${error}`;
      default:
        return 'API: checking...';
    }
  };

  return (
    <Group gap="xs">
      <Badge
        color={getBadgeColor()}
        variant="light"
        size="lg"
      >
        {getBadgeText()}
      </Badge>
      <Tooltip label="Refresh status">
        <ActionIcon
          variant="subtle"
          onClick={refresh}
          loading={status === 'loading'}
          aria-label="Refresh API status"
        >
          <IconRefresh size={16} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}
