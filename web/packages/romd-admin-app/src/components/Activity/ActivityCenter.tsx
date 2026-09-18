import {
  ActionIcon,
  Anchor,
  Badge,
  Box,
  Button,
  Drawer,
  Group,
  Indicator,
  Progress,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { JobDto } from '@romd/admin-api-client';
import {
  IconActivity,
  IconAlertTriangle,
  IconCheck,
  IconClock,
  IconPlayerStop,
  IconRefresh,
} from '@tabler/icons-react';
import { type ReactNode, useMemo, useState } from 'react';
import { Link } from 'react-router';
import { useArchiveJob, useCancelJob, useRetryFailedJob } from '../../hooks/api/useJobActions';
import { useJobs } from '../../hooks/api/useJobs';
import { type ConnectionStatus, useConnectionStatus } from '../../hooks/realtime/JobSocketContext';
import { useKeyboardShortcuts } from '../../hooks/useKeyboardShortcuts';
import { JobStatusBadge } from '../Jobs/JobStatusBadge';
import { formatGroupRange, groupConsecutiveJobs, type JobGroup } from '../Jobs/jobGrouping';
import { getJobTitle, summarizeFailureReasons } from '../Jobs/jobTypeRegistry';

// The panel shows only the most recent completions; older runs live in full history.
const EARLIER_LIMIT = 8;

function connectionColor(status: ConnectionStatus): string {
  if (status === 'connected') return 'var(--mantine-color-green-6)';
  if (status === 'connecting' || status === 'reconnecting') return 'var(--mantine-color-yellow-6)';
  return 'var(--mantine-color-red-6)';
}

function ZoneLabel({ children }: { children: ReactNode }) {
  return (
    <Text size="xs" c="dimmed" fw={600} tt="uppercase" mb={6}>
      {children}
    </Text>
  );
}

/**
 * The Activity slide-over: an ambient, header-triggered panel that consolidates
 * every async job into three zones — what's running, what failed and needs a
 * decision, and what completed earlier. Evolves the old floating job monitor.
 */
export function ActivityCenter() {
  const [opened, setOpened] = useState(false);
  const [retryingId, setRetryingId] = useState<string | null>(null);
  const [dismissingId, setDismissingId] = useState<string | null>(null);

  const { data: jobs = [] } = useJobs();
  const connection = useConnectionStatus();
  const cancelJob = useCancelJob();
  const retryJob = useRetryFailedJob();
  const archiveJob = useArchiveJob();

  useKeyboardShortcuts([
    {
      key: 'j',
      meta: true,
      description: 'Toggle activity panel',
      action: () => setOpened((value) => !value),
    },
  ]);

  const running = useMemo(() => jobs.filter((job) => !job.isTerminal), [jobs]);
  const needsAttention = useMemo(
    () => jobs.filter((job) => job.isTerminal && job.hasErrors && !job.isArchived),
    [jobs],
  );
  const earlier = useMemo(
    () =>
      groupConsecutiveJobs(
        jobs.filter((job) => job.isTerminal && !job.hasErrors && !job.isArchived),
      ).slice(0, EARLIER_LIMIT),
    [jobs],
  );

  const badge = running.length + needsAttention.length;
  const hasActivity = running.length > 0 || needsAttention.length > 0 || earlier.length > 0;

  const handleRetry = async (job: JobDto) => {
    setRetryingId(job.id);
    try {
      const result = await retryJob.mutateAsync(job.id);
      const requeued = Number(result?.requeued ?? 0);
      notifications.show({
        message:
          requeued > 0
            ? `Re-enqueued ${requeued} failed ${requeued === 1 ? 'title' : 'titles'}.`
            : 'No retryable failures remained.',
        color: requeued > 0 ? 'green' : 'gray',
      });
    } catch {
      notifications.show({ color: 'red', message: 'Could not queue the retry.' });
    } finally {
      setRetryingId(null);
    }
  };

  const handleDismiss = async (job: JobDto) => {
    setDismissingId(job.id);
    try {
      await archiveJob.mutateAsync(job.id);
    } catch {
      notifications.show({ color: 'red', message: 'Could not dismiss the job.' });
    } finally {
      setDismissingId(null);
    }
  };

  return (
    <>
      <Indicator label={badge} size={16} disabled={badge === 0} color="blue" offset={4}>
        <ActionIcon
          variant="subtle"
          size="lg"
          aria-label="Activity"
          onClick={() => setOpened(true)}
        >
          <IconActivity size={20} />
        </ActionIcon>
      </Indicator>

      <Drawer
        opened={opened}
        onClose={() => setOpened(false)}
        position="right"
        size="md"
        title={
          <Group gap="xs">
            <Text fw={600}>Activity</Text>
            <Tooltip label={`Realtime: ${connection}`}>
              <Box
                w={8}
                h={8}
                style={{ borderRadius: '50%', backgroundColor: connectionColor(connection) }}
              />
            </Tooltip>
          </Group>
        }
      >
        <Stack gap="lg">
          {!hasActivity && (
            <Text size="sm" c="dimmed" ta="center" py="xl">
              No recent activity.
            </Text>
          )}

          {running.length > 0 && (
            <div>
              <ZoneLabel>Running now</ZoneLabel>
              <Stack gap="sm">
                {running.map((job) => (
                  <RunningRow key={job.id} job={job} onCancel={() => cancelJob.mutate(job.id)} />
                ))}
              </Stack>
            </div>
          )}

          {needsAttention.length > 0 && (
            <div>
              <ZoneLabel>Needs attention</ZoneLabel>
              <Stack gap="sm">
                {needsAttention.map((job) => (
                  <AttentionRow
                    key={job.id}
                    job={job}
                    retrying={retryingId === job.id}
                    dismissing={dismissingId === job.id}
                    onRetry={handleRetry}
                    onDismiss={handleDismiss}
                  />
                ))}
              </Stack>
            </div>
          )}

          {earlier.length > 0 && (
            <div>
              <ZoneLabel>Earlier</ZoneLabel>
              <Stack gap={6}>
                {earlier.map((group) => (
                  <EarlierRow key={group.key} group={group} />
                ))}
              </Stack>
            </div>
          )}

          {hasActivity && (
            <Anchor component={Link} to="/jobs" size="sm" onClick={() => setOpened(false)}>
              Full history
            </Anchor>
          )}
        </Stack>
      </Drawer>
    </>
  );
}

function RunningRow({ job, onCancel }: { job: JobDto; onCancel: () => void }) {
  const progress = Number(job.progressPercent ?? 0);
  const failed = job.errors?.length ?? 0;

  return (
    <Box>
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Text size="sm" fw={500} truncate="end">
          {getJobTitle(job)}
        </Text>
        <Tooltip label="Cancel">
          <ActionIcon variant="subtle" color="red" size="sm" onClick={onCancel}>
            <IconPlayerStop size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>
      <Progress value={progress} size="sm" mt={4} animated />
      <Group justify="space-between" mt={2}>
        <Text size="xs" c="dimmed" truncate="end">
          {job.phase}
        </Text>
        <Text size="xs" c={failed > 0 ? 'red' : 'dimmed'} style={{ flexShrink: 0 }}>
          {progress}%{failed > 0 ? ` · ${failed} failed so far` : ''}
        </Text>
      </Group>
    </Box>
  );
}

interface AttentionRowProps {
  job: JobDto;
  retrying: boolean;
  dismissing: boolean;
  onRetry: (job: JobDto) => void;
  onDismiss: (job: JobDto) => void;
}

function AttentionRow({ job, retrying, dismissing, onRetry, onDismiss }: AttentionRowProps) {
  const failed = job.errors?.length ?? 0;
  const canRetry = job.jobType === 'bulk_enrichment' && Number(job.failedCount ?? 0) > 0;

  return (
    <Box pl="sm" style={{ borderLeft: '2px solid var(--mantine-color-red-6)' }}>
      <Group gap="xs" wrap="nowrap">
        <IconAlertTriangle size={14} color="var(--mantine-color-red-6)" style={{ flexShrink: 0 }} />
        <Text size="sm" fw={500} truncate="end">
          {getJobTitle(job)}
        </Text>
      </Group>
      <Text size="xs" c="dimmed" mt={2}>
        {failed} failed · {summarizeFailureReasons(job.errors ?? [])}
      </Text>
      <Group gap="xs" mt={6}>
        {canRetry && (
          <Button
            size="compact-xs"
            variant="light"
            color="orange"
            leftSection={<IconRefresh size={12} />}
            loading={retrying}
            onClick={() => onRetry(job)}
          >
            Retry {Number(job.failedCount)} failed
          </Button>
        )}
        <Button
          size="compact-xs"
          variant="subtle"
          color="gray"
          loading={dismissing}
          onClick={() => onDismiss(job)}
        >
          Dismiss
        </Button>
      </Group>
    </Box>
  );
}

function EarlierRow({ group }: { group: JobGroup }) {
  const lead = group.jobs[0];
  const count = group.jobs.length;

  return (
    <Group justify="space-between" wrap="nowrap" gap="xs">
      <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
        <Text size="sm" truncate="end">
          {getJobTitle(lead)}
        </Text>
        <JobStatusBadge job={lead} />
        {count > 1 && (
          <Badge size="xs" variant="default" style={{ flexShrink: 0 }}>
            ×{count}
          </Badge>
        )}
      </Group>
      <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>
        {formatGroupRange(group.jobs)}
      </Text>
    </Group>
  );
}
