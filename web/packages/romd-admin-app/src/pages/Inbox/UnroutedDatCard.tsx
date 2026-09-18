import { Anchor, Badge, Button, Checkbox, Group, Paper, Select, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { SystemResourceDto, UnroutedDat } from '@romd/admin-api-client';
import { IconDatabase, IconPlus } from '@tabler/icons-react';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useAssignDatPlatform } from '../../hooks/api/useDats';
import { useAddPlatformAlias } from '../../hooks/api/usePlatformManagement';

export interface UnroutedDatCardProps {
  summary: UnroutedDat;
  platforms: SystemResourceDto[];
  /** Whether the current user can route DATs (Manager+) */
  canRoute: boolean;
  /** Opens the new-system modal seeded with this DAT */
  onCreateSystem: (summary: UnroutedDat, rememberAlias: boolean) => void;
}

/**
 * One unrouted DAT with its routing controls. Assigning routes the DAT and,
 * when "remember" is checked, saves the DAT's header name as a name alias of
 * the chosen system — so the next DAT with this header routes itself.
 */
export function UnroutedDatCard({ summary, platforms, canRoute, onCreateSystem }: UnroutedDatCardProps) {
  const { dat, matchedRomFileCount } = summary;
  const navigate = useNavigate();
  const [selectedPlatformId, setSelectedPlatformId] = useState<string | null>(null);
  const [rememberAlias, setRememberAlias] = useState(true);

  const assignMutation = useAssignDatPlatform();
  const addAliasMutation = useAddPlatformAlias();

  const blockedCount = Number(matchedRomFileCount ?? 0);
  const gameCount = Number(dat.gameCount ?? 0);
  const isAssigning = assignMutation.isPending || addAliasMutation.isPending;

  const handleAssign = async () => {
    if (!selectedPlatformId) return;
    const platform = platforms.find((p) => p.key === selectedPlatformId);

    try {
      await assignMutation.mutateAsync({ datId: dat.id, systemKey: selectedPlatformId });

      if (rememberAlias) {
        try {
          await addAliasMutation.mutateAsync({
            systemKey: selectedPlatformId,
            type: 'name',
            value: dat.name,
          });
        } catch (error) {
          notifications.show({
            title: 'Alias not saved',
            message: error instanceof Error ? error.message : 'Could not save the alias.',
            color: 'yellow',
          });
        }
      }

      const toastId = `routed-${dat.id}`;
      notifications.show({
        id: toastId,
        title: `Routed to ${platform?.name ?? 'system'}`,
        message: (
          <Stack gap={4}>
            <Text size="sm">
              {blockedCount > 0
                ? `${dat.name} assigned — ${blockedCount.toLocaleString()} waiting ROM${blockedCount === 1 ? '' : 's'} now cataloging.`
                : `${dat.name} assigned.`}
            </Text>
            <Anchor
              component="button"
              type="button"
              size="sm"
              onClick={() => {
                notifications.hide(toastId);
                navigate(`/systems/${selectedPlatformId}`);
              }}
            >
              View {platform?.name ?? 'system'} →
            </Anchor>
          </Stack>
        ),
        color: 'green',
      });
    } catch {
      notifications.show({
        title: 'Routing failed',
        message: 'Failed to assign the system. Please try again.',
        color: 'red',
      });
    }
  };

  return (
    <Paper p="lg" radius="md" withBorder>
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap" gap="lg">
          <Group wrap="nowrap" gap="sm" style={{ minWidth: 0 }}>
            <IconDatabase size={20} color="var(--mantine-color-dimmed)" style={{ flexShrink: 0 }} />
            <div style={{ minWidth: 0 }}>
              <Text fw={600} truncate>
                {dat.name}
              </Text>
              <Group gap={8} mt={2}>
                <Badge size="sm" variant="light" color="gray" radius="sm">
                  {dat.type}
                </Badge>
                {dat.version && (
                  <Text size="xs" c="dimmed" style={{ fontFamily: 'var(--mantine-font-family-monospace)' }}>
                    v{dat.version}
                  </Text>
                )}
              </Group>
            </div>
          </Group>

          <Stack gap={0} align="flex-end" style={{ flexShrink: 0 }}>
            <Text size="sm" c="dimmed">
              {gameCount.toLocaleString()} games
            </Text>
            <Text size="sm" fw={600} c={blockedCount > 0 ? 'orange' : 'dimmed'}>
              {blockedCount > 0
                ? `${blockedCount.toLocaleString()} ROM${blockedCount === 1 ? '' : 's'} waiting`
                : 'no ROMs waiting'}
            </Text>
          </Stack>
        </Group>

        {canRoute ? (
          <Stack gap="xs">
            <Group gap="sm" wrap="nowrap">
              <Select
                placeholder="Assign to system..."
                data={platforms.map((p) => ({ value: p.key, label: p.name }))}
                value={selectedPlatformId}
                onChange={setSelectedPlatformId}
                searchable
                disabled={isAssigning}
                style={{ flex: 1, maxWidth: 320 }}
                aria-label="Assign to system"
              />
              <Button onClick={handleAssign} disabled={!selectedPlatformId} loading={isAssigning}>
                Assign
              </Button>
              <Button
                variant="light"
                leftSection={<IconPlus size={16} />}
                onClick={() => onCreateSystem(summary, rememberAlias)}
                disabled={isAssigning}
              >
                New System
              </Button>
            </Group>
            <Checkbox
              size="sm"
              checked={rememberAlias}
              onChange={(e) => setRememberAlias(e.currentTarget.checked)}
              label={`Remember "${dat.name}" as an alias — future DATs with this header route automatically`}
            />
          </Stack>
        ) : (
          <Text size="sm" c="dimmed">
            A Manager can route this DAT to a system.
          </Text>
        )}
      </Stack>
    </Paper>
  );
}
