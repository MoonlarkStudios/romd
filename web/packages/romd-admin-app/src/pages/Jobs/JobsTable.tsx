import { ActionIcon, Alert, Button, Checkbox, Group, Menu, Modal, Progress, Stack, Text } from '@mantine/core';
import type { JobDto } from '@romd/admin-api-client';
import { IconChevronDown, IconX } from '@tabler/icons-react';
import { memo, useCallback, useMemo, useState } from 'react';
import { Link, useLocation } from 'react-router';
import { EmptyState } from '../../components/EmptyState';
import { JobActions } from '../../components/Jobs/JobActions';
import { JobStatusBadge } from '../../components/Jobs/JobStatusBadge';
import { canPerformJobAction, type JobAction } from '../../components/Jobs/jobActionEligibility';
import { formatJobDuration } from '../../components/Jobs/jobFormatting';
import { getJobOutcome, getJobTitle, getJobTypeConfig } from '../../components/Jobs/jobTypeRegistry';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import type { useJobOperations } from '../../hooks/useJobOperations';
import { usePermissions } from '../../hooks/usePermissions';
import classes from './JobsTable.module.css';

type Operations = ReturnType<typeof useJobOperations>;
type BatchResult = NonNullable<Awaited<ReturnType<Operations['runBatch']>>>;
const actionLabels: Record<JobAction, string> = { archive: 'Archive', retry: 'Retry', cancel: 'Cancel' };

function JobResult({ job }: { job: JobDto }) {
  if (getJobOutcome(job) === 'running') return <Stack gap={3}><Text size="xs">{job.phase} · {Math.round(Number(job.progressPercent ?? 0))}%</Text><Progress value={Number(job.progressPercent ?? 0)} size="xs" aria-label="Job progress" /></Stack>;
  if (job.hasErrors) return <Text size="xs" c="red">{job.errors?.length ?? 0} errors · View details</Text>;
  return <Text size="xs" c="dimmed">{job.isArchived ? 'Archived' : job.isTerminal ? 'View details' : 'Waiting to start'}</Text>;
}

export const JobTableRow = memo(function JobTableRow({ job, selected, toggle, operations, historyUrl }: {
  job: JobDto; selected: boolean; toggle: (id: string) => void; operations: Operations; historyUrl: string;
}) {
  const busy = operations.actionsPending;
  return <tr data-selected={selected || undefined}>
        <td><Checkbox aria-label={`Select ${getJobTitle(job)}`} checked={selected} disabled={busy} onChange={() => toggle(job.id)} /></td>
        <td className={classes.job}><Link className={classes.link} to={`/jobs/${job.id}`} state={{ historyUrl: historyUrl }}>{getJobTitle(job)}</Link><Text size="xs" c="dimmed">{getJobTypeConfig(job.jobType).label}</Text><div className={classes.mobile}><JobStatusBadge job={job} /><JobResult job={job} /></div></td>
        <td className={classes.outcome}><JobStatusBadge job={job} /></td><td className={classes.result}><JobResult job={job} /></td>
        <td className={classes.created}>{job.createdAt ? new Date(job.createdAt).toLocaleString() : '—'}</td><td className={classes.duration}>{formatJobDuration(job)}</td>
        <td><JobActions job={job} {...operations} /></td>
      </tr>;
});

export function JobsTable({ jobs, operations, updatedAt }: { jobs: JobDto[]; operations: Operations; updatedAt?: number }) {
  const location = useLocation();
  const { hasRole } = usePermissions();
  const manager = hasRole('Manager');
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [confirmation, setConfirmation] = useState<JobAction | null>(null);
  const [result, setResult] = useState<BatchResult | null>(null);
  const selected = useMemo(() => jobs.filter((job) => selectedIds.has(job.id)), [jobs, selectedIds]);
  const busy = operations.actionsPending;
  const counts = useMemo(() => {
    const result = { archive: 0, retry: 0, cancel: 0 };
    for (const job of selected) for (const action of ['archive', 'retry', 'cancel'] as const) {
      if (canPerformJobAction(job, action, manager)) result[action]++;
    }
    return result;
  }, [selected, manager]);
  const toggle = useCallback((id: string) => setSelectedIds((previous) => { const next = new Set(previous); if (next.has(id)) next.delete(id); else next.add(id); return next; }), []);
  const run = async () => {
    if (!confirmation || busy) return;
    const outcome = await operations.runBatch(selected, confirmation, manager);
    if (outcome) { setResult(outcome); setSelectedIds(new Set(outcome.failed.map((job) => job.id))); setConfirmation(null); }
  };
  return <Stack gap="sm">
    <div className={classes.toolbar} role="region" aria-label="Selection actions">
      <Group gap="sm" wrap="nowrap">
        <Text size="sm" className={classes.count}>{selected.length ? `${selected.length} selected` : `${jobs.length} jobs on this page`}</Text>
        <Text size="xs" c="dimmed" className={classes.updated}>{updatedAt ? `Updated ${new Date(updatedAt).toLocaleTimeString()}` : ''}</Text>
      </Group>
      <Group gap={4} wrap="nowrap">
        <Menu position="bottom-end" shadow="md" width={230}>
          <Menu.Target><Button {...workspaceActionProps} variant="light" size="xs" disabled={busy || !selected.length} rightSection={<IconChevronDown size={14} />}>Actions</Button></Menu.Target>
          <Menu.Dropdown>
            {(['archive', 'retry', 'cancel'] as const).map((action) => counts[action] > 0 && <Menu.Item key={action} color={action === 'cancel' ? 'red' : undefined} onClick={() => setConfirmation(action)}>{actionLabels[action]} selected ({counts[action]})</Menu.Item>)}
            {!Object.values(counts).some(Boolean) && <Menu.Item disabled>No available actions</Menu.Item>}
          </Menu.Dropdown>
        </Menu>
        <ActionIcon aria-label="Clear selection" variant="subtle" color="gray" disabled={busy || !selected.length} onClick={() => setSelectedIds(new Set())}><IconX size={16} /></ActionIcon>
      </Group>
    </div>
    {result && <Alert title={`${actionLabels[result.action]} results`} color={result.failed.length ? 'orange' : 'teal'} withCloseButton onClose={() => setResult(null)}>
      <Text size="sm">{result.succeeded} succeeded · {result.skipped} skipped · {result.failed.length} failed.</Text>
      {result.skipped > 0 && <Text size="xs">Skipped jobs were ineligible or had no retryable failures remaining.</Text>}
      {result.failed.length > 0 && <><Text size="sm">These jobs could not be updated. Inspect their current state before trying again.</Text><ul>{result.failed.map((job) => <li key={job.id}><Link to={`/jobs/${job.id}`} state={{ historyUrl: location.pathname + location.search }}>{getJobTitle(job)}</Link></li>)}</ul></>}
    </Alert>}
    {jobs.length === 0 ? <EmptyState title="No matching jobs" description="Try another outcome, date range, or history filter." /> : <table className={classes.table} aria-label="Job history">
      <thead><tr>
        <th className={classes.selection}><Checkbox aria-label="Select this page" disabled={busy || !jobs.length} checked={!!jobs.length && selected.length === jobs.length} indeterminate={selected.length > 0 && selected.length < jobs.length} onChange={(event) => setSelectedIds(event.currentTarget.checked ? new Set(jobs.map((job) => job.id)) : new Set())} /></th>
        <th>Job</th><th className={classes.outcome}>Outcome</th><th className={classes.result}>Progress / result</th><th className={classes.created}>Created</th><th className={classes.duration}>Duration</th><th className={classes.actions}>Actions</th>
      </tr></thead>
      <tbody>{jobs.map((job) => <JobTableRow key={job.id} job={job} selected={selectedIds.has(job.id)} toggle={toggle} operations={operations} historyUrl={location.pathname + location.search} />)}</tbody>
    </table>}
    <Modal opened={confirmation !== null} onClose={() => { if (!busy) setConfirmation(null); }} title={confirmation ? `${actionLabels[confirmation]} selected jobs` : ''} closeOnClickOutside={!busy} closeOnEscape={!busy} withCloseButton={!busy}>
      {confirmation && <Stack>
        <Text>{counts[confirmation]} eligible jobs will be updated. {selected.length - counts[confirmation]} selected jobs will be skipped.</Text>
        {confirmation === 'archive' && <Text size="sm">Archived records remain accessible in history until scheduled deletion, 30 days after archival.</Text>}
        {confirmation === 'cancel' && <Text size="sm">This stops the selected active jobs. Completed work is not undone.</Text>}
        {confirmation === 'retry' && <Text size="sm">Only retryable failures in bulk enrichment jobs will be queued again.</Text>}
        <Group justify="flex-end"><Button variant="default" disabled={busy} onClick={() => setConfirmation(null)}>Keep selection</Button><Button {...workspaceActionProps} color={confirmation === 'cancel' ? 'red' : 'teal'} loading={busy} disabled={!counts[confirmation]} onClick={() => void run()}>Confirm {actionLabels[confirmation].toLowerCase()}</Button></Group>
      </Stack>}
    </Modal>
  </Stack>;
}
