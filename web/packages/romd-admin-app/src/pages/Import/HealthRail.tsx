import { Divider, Group, Paper, Stack, Text } from '@mantine/core';
import { useLibraryStats } from '../../hooks/api/useRoms';
import { formatBytes } from '../../utils/format';

function RailStat({ label, value, detail, color }: { label: string; value: string; detail: string; color?: string }) {
  return (
    <Stack gap={0} style={{ minWidth: 0 }}>
      <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
        {label}
      </Text>
      <Text size="lg" fw={700} c={color}>
        {value}
      </Text>
      <Text size="xs" c="dimmed" truncate>
        {detail}
      </Text>
    </Stack>
  );
}

export function HealthRail() {
  const { data: stats } = useLibraryStats();

  const stored = Number(stats?.totalRomFiles ?? 0);
  const cataloged = Number(stats?.catalogedCount ?? 0);
  const needsTriage = Number(stats?.unidentifiedCount ?? 0) + Number(stats?.unroutedCount ?? 0);

  return (
    <Paper p="md" radius="md" withBorder>
      <Group gap="xl" wrap="wrap">
        <RailStat
          label="Stored ROMs"
          value={stored.toLocaleString()}
          detail={formatBytes(stats?.totalSizeBytes ?? '0')}
        />
        <Divider orientation="vertical" />
        <RailStat
          label="Cataloged"
          value={cataloged.toLocaleString()}
          detail="Matched to DAT sources"
          color="green"
        />
        <Divider orientation="vertical" />
        <RailStat
          label="Needs triage"
          value={needsTriage.toLocaleString()}
          detail="Unrouted or unidentified"
          color={needsTriage > 0 ? 'orange' : undefined}
        />
      </Group>
    </Paper>
  );
}
