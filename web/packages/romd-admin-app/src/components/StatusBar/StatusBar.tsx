import { Box, Group, Text } from '@mantine/core';
import type { CoverageStats } from '../../hooks/api/useCoverageStats';
import type { HealthStats } from '../../hooks/api/useHealthStats';
import type { StorageStats } from '../../hooks/api/useStorageStats';
import { CoverageSegment } from './CoverageSegment';
import { HealthIndicator } from './HealthIndicator';
import { ServerStatus } from './ServerStatus';
import { StorageSegment } from './StorageSegment';

interface StatusBarProps {
  health: HealthStats | undefined;
  coverage: CoverageStats | undefined;
  storage: StorageStats | undefined;
  isLoading: boolean;
  isError: boolean;
}

export function StatusBar({ health, coverage, storage, isLoading, isError }: StatusBarProps) {
  return (
    <Box
      component="footer"
      style={{
        position: 'fixed',
        bottom: 0,
        left: 0,
        right: 0,
        zIndex: 200,
        borderTop: '1px solid var(--mantine-color-default-border)',
        background: 'var(--mantine-color-body)',
        height: 32,
        display: 'flex',
        alignItems: 'center',
        paddingInline: 'var(--mantine-spacing-md)',
      }}
    >
      <Group
        gap={0}
        wrap="nowrap"
        h="100%"
        style={{
          flex: 1,
          minWidth: 0,
          overflowX: 'auto',
          whiteSpace: 'nowrap',
        }}
      >
        <ServerStatus />
        <StatusDivider />
        <HealthIndicator
          health={health}
          isLoading={isLoading}
          isError={isError}
        />
        <StatusDivider />
        {coverage && !isError ? (
          <CoverageSegment
            coverage={coverage}
            isLoading={isLoading}
          />
        ) : (
          <Text
            size="xs"
            c="dimmed"
            px="sm"
          >
            Coverage: {isLoading ? 'loading' : coverage ? 'stale' : 'unavailable'}
          </Text>
        )}
        <StatusDivider />
        {storage && !isError ? (
          <StorageSegment
            storage={storage}
            isLoading={isLoading}
          />
        ) : (
          <Text
            size="xs"
            c="dimmed"
            px="sm"
          >
            Storage: {isLoading ? 'loading' : storage ? 'stale' : 'unavailable'}
          </Text>
        )}
        <StatusDivider />
      </Group>
    </Box>
  );
}

function StatusDivider() {
  return (
    <Box
      style={{
        width: 1,
        height: 16,
        background: 'var(--mantine-color-default-border)',
        marginInline: 4,
      }}
    />
  );
}
