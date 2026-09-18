import { Box, Group } from '@mantine/core';
import type { CoverageStats } from '../../hooks/api/useCoverageStats';
import type { HealthStats } from '../../hooks/api/useHealthStats';
import type { StorageStats } from '../../hooks/api/useStorageStats';
import { CoverageSegment } from './CoverageSegment';
import { HealthIndicator } from './HealthIndicator';
import { StorageSegment } from './StorageSegment';

interface StatusBarProps {
    health: HealthStats | undefined;
    coverage: CoverageStats | undefined;
    storage: StorageStats | undefined;
    isLoading: boolean;
}

export function StatusBar({ health, coverage, storage, isLoading }: StatusBarProps) {
    return (
        <Box
            component="footer"
            style={{
                position: 'fixed',
                bottom: 0,
                left: 0,
                right: 0,
                zIndex: 200,
                borderTop: '1px solid var(--mantine-color-dark-4)',
                background: 'var(--mantine-color-dark-7)',
                height: 32,
                display: 'flex',
                alignItems: 'center',
                paddingInline: 'var(--mantine-spacing-md)',
            }}
        >
            <Group gap={0} h="100%" style={{ flex: 1 }}>
                <HealthIndicator health={health} isLoading={isLoading} />
                <StatusDivider />
                <CoverageSegment coverage={coverage} isLoading={isLoading} />
                <StatusDivider />
                <StorageSegment storage={storage} isLoading={isLoading} />
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
                background: 'var(--mantine-color-dark-4)',
                marginInline: 4,
            }}
        />
    );
}
