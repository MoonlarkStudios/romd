import { Alert, Anchor, Group, Stack, Text } from '@mantine/core';
import type { OperationalDiagnosticsDto } from '@romd/admin-api-client';
import { Link } from 'react-router';

export function OperationalAttention({ snapshot }: { snapshot: OperationalDiagnosticsDto }) {
  const findings: { key: string; message: string; to: string; action: string }[] = [];
  for (const item of snapshot.catalogProjections.availability === 'Available' ? snapshot.catalogProjections.items : []) {
    if (item.state !== 'Clean') findings.push({ key: `platform-${item.systemKey}`, message: `${item.platformName}: catalog ${item.state.toLowerCase()}. Library updates may wait for the rebuild.`, to: `/systems/${item.systemKey}?tab=sources`, action: 'Review sources' });
  }
  for (const job of snapshot.jobs.availability === 'Available' ? snapshot.jobs.wedgedReplaceDatJobs : []) findings.push({ key: job.jobId, message: 'A source replacement has stopped making progress. Review the latest attempt before taking action.', to: `/jobs/${job.jobId}`, action: 'Inspect replacement' });
  for (const job of snapshot.jobs.availability === 'Available' ? snapshot.jobs.strandedBulkEnrichmentJobs : []) findings.push({ key: job.jobId, message: `${job.platformLabel}: enrichment is waiting for recovery.`, to: `/jobs/${job.jobId}`, action: 'Inspect enrichment' });
  if (snapshot.outbox.availability === 'Available' && snapshot.outbox.failingCount > 0) findings.push({ key: 'outbox', message: `${snapshot.outbox.failingCount} realtime messages are retrying. Live views may be delayed; refresh the affected workspace.`, to: '/jobs', action: 'Review job history' });
  if (snapshot.hangfire.availability === 'Available' && snapshot.hangfire.servers.length === 0) findings.push({ key: 'workers', message: 'No worker servers were reported. Check the worker container and its readiness before retrying jobs.', to: '/jobs?outcome=queued', action: 'View queued jobs' });
  if (snapshot.storage.dataVolumeAvailability === 'Unavailable' || snapshot.storage.casAvailability === 'Unavailable') findings.push({ key: 'storage', message: 'Storage evidence is unavailable. Check the volume mount and worker/admin access before starting more imports.', to: '/storage', action: 'Review storage' });
  const unavailable = [snapshot.catalogProjections, snapshot.jobs, snapshot.outbox, snapshot.hangfire].some((section) => section.availability === 'Unavailable');
  return <Stack gap="sm" component="section" aria-label="Operational attention">
    <Alert color={findings.length ? 'orange' : 'gray'} title={findings.length ? 'Needs attention' : 'No additional findings in this snapshot'}>
      <Stack gap="sm">
        {findings.slice(0, 5).map((finding) => <Group key={finding.key} justify="space-between" align="flex-start"><Text size="sm" style={{ flex: '1 1 300px' }}>{finding.message}</Text><Anchor component={Link} to={finding.to} size="sm">{finding.action}</Anchor></Group>)}
        {findings.length > 5 && <Text size="xs">More findings are listed in the sections below.</Text>}
        <Text size="xs">{unavailable ? 'Some probes were unavailable. ' : ''}This snapshot is bounded; see section availability and truncation notices below. Probe availability does not mean the subsystem is healthy.</Text>
      </Stack>
    </Alert>
  </Stack>;
}
