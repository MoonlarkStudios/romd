import {
    Divider,
    Group,
    Popover,
    Stack,
    Text,
    UnstyledButton,
} from '@mantine/core';
import { IconDatabase } from '@tabler/icons-react';
import type { StorageStats } from '../../hooks/api/useStorageStats';
import { formatBytes } from '../../utils/format';

interface StorageSegmentProps {
    storage: StorageStats | undefined;
    isLoading: boolean;
}

export function StorageSegment({ storage, isLoading }: StorageSegmentProps) {
    const onDisk = storage?.totalStorageBytesOnDisk ?? '0';
    const raw = storage?.totalStorageBytes ?? '0';
    const saved = storage?.bytesSaved ?? '0';
    const ratio = storage?.averageCompressionRatio ?? 1;
    const compressed = storage?.compressedFileCount ?? 0;
    const uncompressed = storage?.uncompressedFileCount ?? 0;

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
                        <IconDatabase
                            size={13}
                            color="var(--mantine-color-blue-5)"
                        />
                        <Text size="xs" c="dimmed">
                            {formatBytes(onDisk)}
                        </Text>
                    </Group>
                </UnstyledButton>
            </Popover.Target>

            <Popover.Dropdown>
                <Stack gap="sm">
                    <Text size="sm" fw={600}>
                        Storage
                    </Text>

                    <StatRow label="On disk" value={formatBytes(onDisk)} />
                    <StatRow label="Raw size" value={formatBytes(raw)} />
                    <StatRow
                        label="Saved"
                        value={formatBytes(saved)}
                        color="green"
                    />
                    <StatRow
                        label="Compression ratio"
                        value={ratio.toFixed(2)}
                    />

                    <Divider my={4} />

                    <StatRow
                        label="Compressed files"
                        value={compressed.toString()}
                    />
                    <StatRow
                        label="Uncompressed files"
                        value={uncompressed.toString()}
                    />
                </Stack>
            </Popover.Dropdown>
        </Popover>
    );
}

function StatRow({
    label,
    value,
    color,
}: {
    label: string;
    value: string;
    color?: string;
}) {
    return (
        <Group justify="space-between">
            <Text size="xs" c="dimmed">
                {label}
            </Text>
            <Text size="xs" fw={600} c={color}>
                {value}
            </Text>
        </Group>
    );
}
