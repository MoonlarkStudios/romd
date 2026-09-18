import { ActionIcon, Collapse, Group, Paper, Progress, Stack, Text } from '@mantine/core';
import { IconChevronDown, IconChevronRight } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useLocation } from 'react-router';
import { ProvenanceLedger } from '../../components/Import/ProvenanceLedger';
import { JobStatusBadge } from '../../components/Jobs/JobStatusBadge';
import {
  getJobOutcome,
  getJobTitle,
  summarizeFailureReasons,
} from '../../components/Jobs/jobTypeRegistry';
import workspace from '../../components/Workspace/Workspace.module.css';
import { JobActions, type JobActionsProps } from './JobActions';
import classes from './JobRows.module.css';
import { formatJobDuration } from './jobFormatting';


function formatDate(dateString?: string | null): string {
  if (!dateString) return '-';
  return new Date(dateString).toLocaleString();
}

export function JobRow({ job, onCancel, onArchive, onRetry, retryingId, actionsPending }: JobActionsProps) {
  const location = useLocation();
  const isRunning = !job.isTerminal;
  const progress = Number(job.progressPercent ?? 0);
  // Only upload jobs produce per-file provenance; other job types have nothing to drill into.
  const canInspect = job.jobType === 'upload';
  const [expanded, setExpanded] = useState(false);

  return (
    <Paper className={workspace.panel}>
      <Group justify="space-between" wrap="nowrap">
        <Stack gap={6} style={{ flex: 1, minWidth: 0 }}>
          <Group gap="sm" wrap="wrap">
            {canInspect && (
              <ActionIcon
                variant="subtle"
                color="gray"
                size="sm"
                onClick={() => setExpanded((value) => !value)}
                aria-expanded={expanded}
                aria-label={expanded ? 'Hide files' : 'Show files'}
              >
                {expanded ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
              </ActionIcon>
            )}
            <JobStatusBadge job={job} />
            <Text component={Link} className={classes.title} to={`/jobs/${job.id}`} state={{ historyUrl: location.pathname === "/jobs" ? location.pathname + location.search : location.state?.historyUrl }} size="sm" fw={500} truncate="end">
              {getJobTitle(job)}
            </Text>
          </Group>

          <Text size="xs" c="dimmed">
            {formatDate(job.startedAt ?? job.createdAt)} · {formatJobDuration(job)}
            {isRunning && job.phase ? ` · ${job.phase}` : ''}
          </Text>

          {getJobOutcome(job) === 'running' && (
            <Progress value={progress} size="sm" color="blue" animated />
          )}

          {job.hasErrors && job.errors && job.errors.length > 0 && (
            <Text size="xs" c="red">
              {job.errors.length} failed · {summarizeFailureReasons(job.errors)}
            </Text>
          )}

        </Stack>

        <JobActions job={job} onCancel={onCancel} onArchive={onArchive} onRetry={onRetry} retryingId={retryingId} actionsPending={actionsPending} />
      </Group>

      {canInspect && (
        <Collapse in={expanded}>
          {expanded && (
            <div style={{ marginTop: 'var(--mantine-spacing-sm)' }}>
              <ProvenanceLedger jobId={job.id} />
            </div>
          )}
        </Collapse>
      )}
    </Paper>
  );
}

