import {
    Box,
    Group,
    Popover,
    Progress,
    Stack,
    Text,
    UnstyledButton,
} from '@mantine/core';
import { IconDisc } from '@tabler/icons-react';
import type { CoverageStats } from '../../hooks/api/useCoverageStats';

interface CoverageSegmentProps {
    coverage: CoverageStats | undefined;
    isLoading: boolean;
}

export function CoverageSegment({ coverage, isLoading }: CoverageSegmentProps) {
    const percent = coverage?.coverageHealthPercent ?? 0;
    const localPayload = coverage?.localPayloadTitleCount ?? 0;
    const expected = coverage?.expectedTitleCount ?? 0;
    const complete = coverage?.completeTitleCount ?? 0;
    const partial = coverage?.partialTitleCount ?? 0;

    return (
        <Popover
            position="top-start"
            shadow="lg"
            width={320}
            closeOnClickOutside
        >
            <Popover.Target>
                <UnstyledButton px="sm" h="100%" style={{ borderRadius: 0 }}>
                    <Group gap={6} wrap="nowrap">
                        <IconDisc
                            size={13}
                            color="var(--mantine-color-teal-5)"
                        />
                        <Text size="xs" c="dimmed">
                            {percent.toFixed(1)}%
                        </Text>
                    </Group>
                </UnstyledButton>
            </Popover.Target>

            <Popover.Dropdown>
                <CoverageDetail
                    percent={percent}
                    localPayload={localPayload}
                    expected={expected}
                    complete={complete}
                    partial={partial}
                />
            </Popover.Dropdown>
        </Popover>
    );
}

function CoverageDetail({
    percent,
    localPayload,
    expected,
    complete,
    partial,
}: {
    percent: number;
    localPayload: number;
    expected: number;
    complete: number;
    partial: number;
}) {
    const missing = expected - localPayload;
    const completePercent = expected > 0 ? (complete / expected) * 100 : 0;
    const partialPercent = expected > 0 ? (partial / expected) * 100 : 0;

    return (
        <Stack gap="md">
            <div>
                <Text size="sm" fw={600} mb={4}>
                    Collection Coverage
                </Text>
                <Text size="xs" c="dimmed">
                    {localPayload.toLocaleString()} of {expected.toLocaleString()}{' '}
                    known titles
                </Text>
            </div>

            {/* Stacked progress bar showing complete/partial/missing */}
            <div>
                <Progress.Root size="xl" radius="md">
                    <Progress.Section value={completePercent} color="green">
                        {completePercent > 10 && (
                            <Progress.Label>{complete}</Progress.Label>
                        )}
                    </Progress.Section>
                    <Progress.Section value={partialPercent} color="orange">
                        {partialPercent > 10 && (
                            <Progress.Label>{partial}</Progress.Label>
                        )}
                    </Progress.Section>
                </Progress.Root>

                <Group justify="space-between" mt="xs">
                    <Group gap="lg">
                        <Group gap={4}>
                            <Box
                                w={8}
                                h={8}
                                bg="green"
                                style={{ borderRadius: 2 }}
                            />
                            <Text size="xs" c="dimmed">
                                Complete ({complete})
                            </Text>
                        </Group>
                        <Group gap={4}>
                            <Box
                                w={8}
                                h={8}
                                bg="orange"
                                style={{ borderRadius: 2 }}
                            />
                            <Text size="xs" c="dimmed">
                                Partial ({partial})
                            </Text>
                        </Group>
                        <Group gap={4}>
                            <Box
                                w={8}
                                h={8}
                                bg="var(--mantine-color-dark-3)"
                                style={{ borderRadius: 2 }}
                            />
                            <Text size="xs" c="dimmed">
                                Missing ({missing})
                            </Text>
                        </Group>
                    </Group>
                </Group>
            </div>

            <Text size="xl" fw={700} ta="center">
                {percent.toFixed(1)}%
            </Text>
        </Stack>
    );
}
