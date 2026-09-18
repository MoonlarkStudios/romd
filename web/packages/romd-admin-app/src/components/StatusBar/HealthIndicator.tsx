import { Group, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconCircleFilled } from '@tabler/icons-react';
import type { HealthStats } from '../../hooks/api/useHealthStats';

interface HealthIndicatorProps {
    health: HealthStats | undefined;
    isLoading: boolean;
}

export function HealthIndicator({ health, isLoading }: HealthIndicatorProps) {
    const issues =
        (health?.unidentifiedCount ?? 0) + (health?.unroutedCount ?? 0);
    const healthy = issues === 0;

    return (
        <Tooltip
            label={
                healthy
                    ? 'All systems healthy'
                    : `${issues} items need attention`
            }
        >
            <UnstyledButton px="sm" h="100%" style={{ borderRadius: 0 }}>
                <Group gap={6} wrap="nowrap">
                    <IconCircleFilled
                        size={8}
                        color={
                            isLoading
                                ? 'var(--mantine-color-gray-5)'
                                : healthy
                                  ? 'var(--mantine-color-green-5)'
                                  : 'var(--mantine-color-orange-5)'
                        }
                    />
                    <Text size="xs" c="dimmed">
                        {isLoading
                            ? '...'
                            : healthy
                              ? 'Healthy'
                              : `${issues} issues`}
                    </Text>
                </Group>
            </UnstyledButton>
        </Tooltip>
    );
}
