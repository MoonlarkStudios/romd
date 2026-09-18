import {
  Alert,
  Box,
  Button,
  Center,
  Group,
  Paper,
  RingProgress,
  SimpleGrid,
  Skeleton,
  Stack,
  Table,
  Text,
  ThemeIcon,
  Title,
} from '@mantine/core';
import { IconDatabase, IconFileZip } from '@tabler/icons-react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useStorageStats } from '../../hooks/api/useStorageStats';
import { usePermissions } from '../../hooks/usePermissions';
import { formatBytes, integerPercentage } from '../../utils/format';
import { StorageCapacity } from './StorageCapacity';

/** Display labels for the CAS attribution categories (keys are stable backend keys). */
const STORAGE_CATEGORY_LABELS: Record<string, string> = {
  rom_matched: 'ROMs (matched)',
  rom_unmatched: 'ROMs (unmatched)',
  dat: 'DAT files',
  media: 'Media',
  unattributed: 'Unattributed',
};

export function Storage() {
  const query = useStorageStats();
  const { hasRole } = usePermissions();
  const { data: storage } = query;

  const rawSize = storage?.totalStorageBytes ?? '0';
  const onDiskSize = storage?.totalStorageBytesOnDisk ?? '0';
  const bytesSaved = storage?.bytesSaved ?? '0';
  const ratio = storage?.averageCompressionRatio ?? 1;
  const compressedFiles = storage?.compressedFileCount ?? 0;
  const uncompressedFiles = storage?.uncompressedFileCount ?? 0;
  const breakdown = storage?.breakdown ?? [];
  const totalFiles = compressedFiles + uncompressedFiles;
  const savedPercent = integerPercentage(bytesSaved, rawSize);

  return (
    <Stack className={workspace.page} gap="lg">
      <Group className={workspace.hero} justify="space-between">
      <Box>
        <Title order={1} className={workspace.heading}>Storage</Title>
        <Text c="dimmed" size="sm">
          Registered content and compression efficiency.
        </Text>
      </Box>
      <Button {...workspaceActionProps} variant="light" loading={query.isFetching} onClick={() => void query.refetch()}>Refresh</Button>
      </Group>
      {hasRole('Admin') && <StorageCapacity />}
      {query.isError && <Alert color="red" title={storage ? 'Could not refresh storage' : 'Could not load storage'}>{storage ? 'Showing the last available metrics. Refresh to try again.' : 'Refresh to try again.'}</Alert>}
      {query.dataUpdatedAt > 0 && <Text size="xs" c="dimmed">Last updated {new Date(query.dataUpdatedAt).toLocaleString()}</Text>}
      {query.isPending ? <Stack role="status" aria-label="Loading storage"><Skeleton h={140} /><Skeleton h={140} /></Stack> : storage && <>
      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
        <Paper className={workspace.panel}>
          <Group justify="space-between" align="center">
            <Stack gap={4}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
                Stored size
              </Text>
              <Text size="xl" fw={700}>
                {formatBytes(onDiskSize)}
              </Text>
              <Text size="xs" c="dimmed">
                {totalFiles.toLocaleString()} registered objects
              </Text>
            </Stack>
            <ThemeIcon size={48} radius="md" variant="light" color="blue">
              <IconDatabase size={24} />
            </ThemeIcon>
          </Group>
        </Paper>

        <Paper className={workspace.panel}>
          <Group justify="space-between" align="center">
            <Stack gap={4}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
                Raw Uncompressed
              </Text>
              <Text size="xl" fw={700}>
                {formatBytes(rawSize)}
              </Text>
              <Text size="xs" c="dimmed">
                Logical size of registered objects
              </Text>
            </Stack>
            <ThemeIcon size={48} radius="md" variant="light" color="gray">
              <IconDatabase size={24} />
            </ThemeIcon>
          </Group>
        </Paper>

        <Paper className={workspace.panel}>
          <Group justify="space-between" align="center">
            <Stack gap={4}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
                Space Saved
              </Text>
              <Text size="xl" fw={700} c="green">
                {formatBytes(bytesSaved)}
              </Text>
              <Text size="xs" c="dimmed">
                {ratio.toFixed(2)} compression ratio
              </Text>
            </Stack>
            <RingProgress
              size={64}
              thickness={6}
              roundCaps
              sections={[{ value: savedPercent, color: 'green' }]}
              label={
                <Center>
                  <IconFileZip size={20} color="var(--mantine-color-green-6)" />
                </Center>
              }
            />
          </Group>
        </Paper>
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
        <Paper className={workspace.panel}>
          <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
            Compressed Files
          </Text>
          <Text size="xl" fw={700} mt={4}>
            {compressedFiles.toLocaleString()}
          </Text>
        </Paper>

        <Paper className={workspace.panel}>
          <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
            Uncompressed Files
          </Text>
          <Text size="xl" fw={700} mt={4}>
            {uncompressedFiles.toLocaleString()}
          </Text>
        </Paper>
      </SimpleGrid>

      {breakdown.length > 0 && (
        <Paper className={workspace.panel}>
          <Text size="xs" c="dimmed" tt="uppercase" fw={700} mb="sm">
            Storage by type
          </Text>
          <Table.ScrollContainer minWidth={520}><Table verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Type</Table.Th>
                <Table.Th ta="right">Files</Table.Th>
                <Table.Th ta="right">On disk</Table.Th>
                <Table.Th ta="right">Logical</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {breakdown.map((category) => (
                <Table.Tr key={category.category}>
                  <Table.Td>
                    {STORAGE_CATEGORY_LABELS[category.category] ?? category.category}
                  </Table.Td>
                  <Table.Td ta="right" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {category.fileCount.toLocaleString()}
                  </Table.Td>
                  <Table.Td ta="right" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {formatBytes(category.sizeOnDiskBytes)}
                  </Table.Td>
                  <Table.Td ta="right" c="dimmed" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {formatBytes(category.sizeBytes)}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table></Table.ScrollContainer>
          <Text size="xs" c="dimmed" mt="xs">
            Categories may overlap when a stored file is shared across owners.
          </Text>
        </Paper>
      )}
      <Text size="xs" c="dimmed">Metrics describe registered content; they are not a filesystem integrity scan. Attribution categories can overlap and must not be added together.</Text>
      </>}
    </Stack>
  );
}
