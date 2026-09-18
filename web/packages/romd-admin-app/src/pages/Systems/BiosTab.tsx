import { ActionIcon, Badge, Group, Menu, Paper, Stack, Table, Text } from '@mantine/core';
import type { Bios } from '@romd/admin-api-client';
import { IconCpu, IconDots, IconRefresh } from '@tabler/icons-react';
import { AsyncBoundary } from '../../components/AsyncBoundary';
import { EmptyState } from '../../components/EmptyState';
import { useBackfillBiosCatalog, useBiosByPlatform } from '../../hooks/api/useBios';
import { formatBytes } from '../../utils/format';

function getStatusBadge(bios: Bios) {
  if (bios.isOwned) {
    return (
      <Badge color="green" size="sm" radius="sm">
        Owned
      </Badge>
    );
  }
  if (Number(bios.ownedRoms ?? 0) > 0) {
    return (
      <Badge color="yellow" size="sm" radius="sm">
        Partial
      </Badge>
    );
  }
  return (
    <Badge color="red" size="sm" radius="sm">
      Missing
    </Badge>
  );
}

interface BiosTableProps {
  bios: Bios[];
}

function BiosTable({ bios }: BiosTableProps) {
  return (
    <Paper radius="md" withBorder style={{ overflow: 'hidden' }}>
      <Table verticalSpacing="sm" highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Name</Table.Th>
            <Table.Th ta="right" w={120}>
              ROMs owned
            </Table.Th>
            <Table.Th ta="right" w={150}>
              Size on disk
            </Table.Th>
            <Table.Th w={120}>Status</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {bios.map((entry) => (
            <Table.Tr key={entry.id}>
              <Table.Td>
                <Text size="sm" fw={500}>
                  {entry.name}
                </Text>
              </Table.Td>
              <Table.Td ta="right">
                <Text size="sm" fw={500} style={{ fontVariantNumeric: 'tabular-nums' }}>
                  {Number(entry.ownedRoms ?? 0).toLocaleString()}/
                  {Number(entry.totalRoms ?? 0).toLocaleString()}
                </Text>
              </Table.Td>
              <Table.Td ta="right">
                <Text size="sm" fw={500} style={{ fontVariantNumeric: 'tabular-nums' }}>
                  {formatBytes(entry.onDiskBytes ?? '0')}
                </Text>
                <Text size="xs" c="dimmed" style={{ fontVariantNumeric: 'tabular-nums' }}>
                  of {formatBytes(entry.requiredBytes ?? '0')}
                </Text>
              </Table.Td>
              <Table.Td>{getStatusBadge(entry)}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Paper>
  );
}

interface BiosTabProps {
  systemKey: string;
}

/**
 * Firmware-readiness lens for a system: which BIOS this platform needs and how much you own
 * (Owned / Partial / Missing). Browsing the BIOS DAT entries themselves lives in Sources.
 * Grouping is automatic on DAT import/assignment; the maintenance re-sync only backfills
 * BIOS from DATs assigned before grouping existed.
 */
export function BiosTab({ systemKey }: BiosTabProps) {
  const biosQuery = useBiosByPlatform(systemKey);
  const backfill = useBackfillBiosCatalog();

  const bios = biosQuery.data ?? [];
  const ownedCount = bios.filter((entry) => entry.isOwned).length;

  return (
    <Stack gap="md">
      <Group justify="space-between" align="center">
        <Text size="sm" c="dimmed">
          {bios.length > 0 ? (
            <>
              <Text component="span" fw={600}>
                {ownedCount.toLocaleString()}
              </Text>{' '}
              of{' '}
              <Text component="span" fw={600}>
                {bios.length.toLocaleString()}
              </Text>{' '}
              BIOS owned
            </>
          ) : (
            'Firmware required by emulators for this system.'
          )}
        </Text>
        <Menu position="bottom-end" withArrow>
          <Menu.Target>
            <ActionIcon
              variant="subtle"
              color="gray"
              aria-label="BIOS maintenance"
              loading={backfill.isPending}
            >
              <IconDots size={18} />
            </ActionIcon>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Maintenance</Menu.Label>
            <Menu.Item
              leftSection={<IconRefresh size={16} />}
              disabled={backfill.isPending}
              onClick={() => backfill.mutate()}
            >
              Re-sync BIOS catalog
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>

      <AsyncBoundary
        query={biosQuery}
        emptyFallback={
          <EmptyState
            icon={<IconCpu size={32} />}
            title="No BIOS entries"
            description="Assign a DAT containing BIOS images to this system, or sync the catalog to group BIOS from DATs assigned earlier."
            action={{
              label: 'Sync BIOS catalog',
              icon: <IconRefresh size={16} />,
              onClick: () => backfill.mutate(),
            }}
          />
        }
      >
        {(entries) => <BiosTable bios={entries} />}
      </AsyncBoundary>
    </Stack>
  );
}
