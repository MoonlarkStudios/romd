import {
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Group,
  Paper,
  SimpleGrid,
  Skeleton,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import type {
  CatalogProjectionDiagnosticsDto,
  CatalogProjectionState,
  DiagnosticAvailability,
  HangfireDiagnosticsDto,
  OperationalJobDiagnosticsDto,
  OutboxDiagnosticsDto,
  RecurringJobDiagnosticState,
  StorageDiagnosticsDto,
} from '@romd/admin-api-client';
import {
  IconAlertCircle,
  IconCheck,
  IconClock,
  IconRefresh,
  IconServer,
} from '@tabler/icons-react';
import { type ReactNode, useState } from 'react';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useOperationalDiagnostics } from '../../hooks/api/useOperationalDiagnostics';
import { formatBytes, formatDuration } from '../../utils/format';
import { OperationalAttention } from './OperationalAttention';

function formatCount(value: string | number): string {
  try {
    return typeof value === 'number' ? value.toLocaleString() : BigInt(value).toLocaleString();
  } catch {
    return String(value);
  }
}

function formatAge(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return 'Unknown';
  }

  return formatDuration(value);
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return 'Never';
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function formatByteCount(value: string | null | undefined): string {
  if (value === null || value === undefined) {
    return 'Unknown';
  }

  return formatBytes(value);
}

function AvailabilityBadge({ availability }: { availability: DiagnosticAvailability }) {
  const available = availability === 'Available';
  return (
    <Badge color={available ? 'green' : 'red'} variant="light">
      {availability}
    </Badge>
  );
}

interface SectionProps {
  title: string;
  description: string;
  availability?: DiagnosticAvailability;
  error?: string | null;
  children: ReactNode;
}

function DiagnosticsSection({
  title,
  description,
  availability,
  error,
  children,
}: SectionProps) {
  const headingId = `diagnostics-${title.toLowerCase().replaceAll(' ', '-')}`;

  return (
    <Paper component="section" aria-labelledby={headingId} className={workspace.panel}>
      <Stack gap="md">
        <Group justify="space-between" align="flex-start" wrap="wrap">
          <Box>
            <Title id={headingId} order={3} className={workspace.sectionHeading}>
              {title}
            </Title>
            <Text c="dimmed" size="sm">
              {description}
            </Text>
          </Box>
          {availability && <AvailabilityBadge availability={availability} />}
        </Group>

        {availability === 'Unavailable' && (
          <Alert color="red" icon={<IconAlertCircle size={16} />} title={`${title} unavailable`}>
            {error || 'This probe did not complete within the bounded diagnostics window.'}
          </Alert>
        )}

        {children}
      </Stack>
    </Paper>
  );
}

function EmptyFact({ children }: { children: ReactNode }) {
  return (
    <Alert color="green" variant="light" icon={<IconCheck size={16} />}>
      {children}
    </Alert>
  );
}

function TruncatedNotice({ subject }: { subject: string }) {
  return (
    <Alert color="yellow" variant="light" icon={<IconAlertCircle size={16} />}>
      Showing a bounded subset; additional {subject} were omitted.
    </Alert>
  );
}

const catalogStateColors: Record<CatalogProjectionState, string> = {
  Clean: 'green',
  Dirty: 'yellow',
  Failed: 'red',
};

function CatalogProjectionsSection({ diagnostics }: { diagnostics: CatalogProjectionDiagnosticsDto }) {
  const [showClean, setShowClean] = useState(false);
  const cleanCount = diagnostics.items.filter((item) => item.state === 'Clean').length;
  const visibleItems = diagnostics.items.filter((item) => showClean || item.state !== 'Clean');
  return (
    <DiagnosticsSection
      title="Catalog projections"
      description="Persisted rebuild state by platform. Dirty and failed projections can defer library materialization."
      availability={diagnostics.availability}
      error={diagnostics.error}
    >
      {cleanCount > 0 && (
        <Group justify="space-between">
          <Text size="sm" c="dimmed">{cleanCount} clean projections in this snapshot.</Text>
          <Button {...workspaceActionProps} variant="light" onClick={() => setShowClean((value) => !value)} aria-expanded={showClean}>
            {showClean ? 'Hide clean projections' : 'Show clean projections'}
          </Button>
        </Group>
      )}
      {diagnostics.items.length === 0 && diagnostics.availability === 'Available' ? (
        <EmptyFact>No catalog projections were reported.</EmptyFact>
      ) : visibleItems.length > 0 ? (
        <Table.ScrollContainer minWidth={720}>
          <Table verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Platform</Table.Th>
                <Table.Th>State</Table.Th>
                <Table.Th>Last rebuild</Table.Th>
                <Table.Th>Failure</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {visibleItems.map((projection) => (
                <Table.Tr key={projection.systemKey}>
                  <Table.Td>
                    <Anchor component={Link} to={`/systems/${projection.systemKey}?tab=sources`} fw={500} size="sm">
                      {projection.platformName}
                    </Anchor>
                    <Text c="dimmed" size="xs">
                      {projection.systemKey} · {projection.systemKey}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Badge color={catalogStateColors[projection.state]} variant="light">
                      {projection.state}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{formatDateTime(projection.rebuiltAt)}</Text>
                  </Table.Td>
                  <Table.Td>
                    {projection.lastError ? (
                      <Stack gap={2}>
                        <Text size="sm" c="red">
                          {projection.lastError}
                        </Text>
                        <Text size="xs" c="dimmed">
                          Failed {formatDateTime(projection.failedAt)}
                        </Text>
                      </Stack>
                    ) : (
                      <Text size="sm" c="dimmed">
                        None recorded
                      </Text>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      ) : null}

      {diagnostics.isTruncated && <TruncatedNotice subject="catalog projections" />}
    </DiagnosticsSection>
  );
}

function JobsSection({ diagnostics }: { diagnostics: OperationalJobDiagnosticsDto }) {
  return (
    <DiagnosticsSection
      title="Job recovery"
      description="Non-terminal work that has crossed the recovery threshold and still needs convergence."
      availability={diagnostics.availability}
      error={diagnostics.error}
    >
      <Stack gap="sm">
        <Title order={4} size="h5">
          Wedged DAT replacements
        </Title>
        {diagnostics.wedgedReplaceDatJobs.length === 0 &&
        diagnostics.availability === 'Available' ? (
          <EmptyFact>No wedged DAT replacement jobs.</EmptyFact>
        ) : diagnostics.wedgedReplaceDatJobs.length > 0 ? (
          diagnostics.wedgedReplaceDatJobs.map((job) => (
            <Paper key={job.jobId} withBorder p="md" radius="sm">
              <Stack gap="xs">
                <Group justify="space-between" align="flex-start" wrap="wrap">
                  <Box>
                    <Anchor component={Link} to={`/jobs/${job.jobId}`} fw={600}>Inspect job {job.jobId}</Anchor>
                    <Text size="xs" c="dimmed">
                      DAT {job.existingDatId}
                      {job.systemKey ? ` · platform ${job.systemKey}` : ''}
                    </Text>
                  </Box>
                  <Badge color="orange" variant="light">
                    {job.phase}
                  </Badge>
                </Group>
                <Group gap="lg" wrap="wrap">
                  <Text size="sm">
                    <Text span c="dimmed">
                      No progress for{' '}
                    </Text>
                    {formatAge(job.stalledForSeconds)}
                  </Text>
                  <Text size="sm">
                    <Text span c="dimmed">
                      Last progress{' '}
                    </Text>
                    {formatDateTime(job.lastProgressAt)}
                  </Text>
                </Group>
                {job.attemptError && (
                  <Alert color="red" variant="light" title="Latest attempt error">
                    <Text size="sm">{job.attemptError}</Text>
                    {job.attemptErrorTruncated && (
                      <Text size="xs" fw={600} mt={4}>
                        Error text was truncated by the bounded diagnostics query.
                      </Text>
                    )}
                  </Alert>
                )}
              </Stack>
            </Paper>
          ))
        ) : null}
        {diagnostics.wedgedReplaceDatJobsTruncated && (
          <TruncatedNotice subject="wedged DAT replacement jobs" />
        )}
      </Stack>

      <Stack gap="sm">
        <Title order={4} size="h5">
          Stranded enrichment
        </Title>
        {diagnostics.strandedBulkEnrichmentJobs.length === 0 &&
        diagnostics.availability === 'Available' ? (
          <EmptyFact>No stranded bulk enrichment jobs.</EmptyFact>
        ) : diagnostics.strandedBulkEnrichmentJobs.length > 0 ? (
          diagnostics.strandedBulkEnrichmentJobs.map((job) => (
            <Paper key={job.jobId} withBorder p="md" radius="sm">
              <Group justify="space-between" align="flex-start" wrap="wrap">
                <Box>
                  <Anchor component={Link} to={`/jobs/${job.jobId}`} fw={600}>{job.platformLabel}</Anchor>
                  <Text size="xs" c="dimmed">
                    {job.jobId}
                    {job.systemKey ? ` · platform ${job.systemKey}` : ''}
                  </Text>
                </Box>
                <Stack gap={2} align="flex-end">
                  <Badge color="orange" variant="light">
                    {job.phase}
                  </Badge>
                  <Text size="xs" c="dimmed">
                    Stranded for {formatAge(job.strandedForSeconds)}
                  </Text>
                </Stack>
              </Group>
            </Paper>
          ))
        ) : null}
        {diagnostics.strandedBulkEnrichmentJobsTruncated && (
          <TruncatedNotice subject="stranded enrichment jobs" />
        )}
      </Stack>
    </DiagnosticsSection>
  );
}

function Metric({ label, value, tone }: { label: string; value: string; tone?: string }) {
  return (
    <Paper withBorder p="md" radius="sm">
      <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
        {label}
      </Text>
      <Text size="lg" fw={700} c={tone} mt={4}>
        {value}
      </Text>
    </Paper>
  );
}

function OutboxSection({ diagnostics }: { diagnostics: OutboxDiagnosticsDto }) {
  return (
    <DiagnosticsSection
      title="Realtime outbox"
      description="Delivery backlog and the last durable realtime message processed."
      availability={diagnostics.availability}
      error={diagnostics.error}
    >
      {diagnostics.pendingCount === 0 && diagnostics.availability === 'Available' && (
        <EmptyFact>No pending outbox messages.</EmptyFact>
      )}
      {diagnostics.availability === 'Available' && (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }}>
          <Metric label="Pending" value={formatCount(diagnostics.pendingCount)} />
          <Metric
            label="Retrying"
            value={formatCount(diagnostics.failingCount)}
            tone={diagnostics.failingCount === 0 ? undefined : 'red'}
          />
          <Metric
            label="Oldest pending"
            value={
              diagnostics.oldestPendingAgeSeconds === null ||
              diagnostics.oldestPendingAgeSeconds === undefined
                ? 'None'
                : formatAge(diagnostics.oldestPendingAgeSeconds)
            }
          />
          <Metric label="Last processed" value={formatDateTime(diagnostics.lastProcessedAt)} />
        </SimpleGrid>
      )}
    </DiagnosticsSection>
  );
}

const recurringStateColors: Record<RecurringJobDiagnosticState, string> = {
  Unknown: 'gray',
  Awaiting: 'blue',
  Scheduled: 'blue',
  Enqueued: 'cyan',
  Processing: 'yellow',
  Succeeded: 'green',
  Failed: 'red',
  Deleted: 'gray',
};

function HangfireSection({ diagnostics }: { diagnostics: HangfireDiagnosticsDto }) {
  return (
    <DiagnosticsSection
      title="Workers and queues"
      description="Hangfire server heartbeats, recurring recovery work, and current queue depth."
      availability={diagnostics.availability}
      error={diagnostics.error}
    >
      <Stack gap="sm">
        <Title order={4} size="h5">
          Worker servers
        </Title>
        {diagnostics.servers.length === 0 && diagnostics.availability === 'Available' ? (
          <Alert color="yellow" variant="light" icon={<IconServer size={16} />}>
            No Hangfire servers were reported.
          </Alert>
        ) : diagnostics.servers.length > 0 ? (
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
            {diagnostics.servers.map((server) => (
              <Paper key={server.name} withBorder p="md" radius="sm">
                <Group justify="space-between" align="flex-start" wrap="wrap">
                  <Box>
                    <Text fw={600}>{server.name}</Text>
                    <Text size="xs" c="dimmed">
                      {formatCount(server.workerCount)} workers · {server.queues.join(', ') || 'No queues'}
                    </Text>
                  </Box>
                  <Badge color="blue" variant="light" leftSection={<IconClock size={12} />}>
                    Heartbeat {formatAge(server.heartbeatAgeSeconds)} old
                  </Badge>
                </Group>
                <Text size="xs" c="dimmed" mt="xs">
                  Last heartbeat {formatDateTime(server.heartbeatAt)} · started{' '}
                  {formatDateTime(server.startedAt)}
                </Text>
              </Paper>
            ))}
          </SimpleGrid>
        ) : null}
        {diagnostics.serversTruncated && <TruncatedNotice subject="worker servers" />}
      </Stack>

      <Stack gap="sm">
        <Title order={4} size="h5">
          Recurring jobs
        </Title>
        {diagnostics.recurringJobs.length === 0 && diagnostics.availability === 'Available' ? (
          <Alert color="yellow" variant="light" icon={<IconClock size={16} />}>
            No recurring jobs were reported.
          </Alert>
        ) : diagnostics.recurringJobs.length > 0 ? (
          <Table.ScrollContainer minWidth={780}>
            <Table verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Job</Table.Th>
                  <Table.Th>Queue / schedule</Table.Th>
                  <Table.Th>Last state</Table.Th>
                  <Table.Th>Last / next execution</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {diagnostics.recurringJobs.map((job) => (
                  <Table.Tr key={job.id}>
                    <Table.Td>
                      <Text fw={500} size="sm">
                        {job.id}
                      </Text>
                      {job.error && (
                        <Text size="xs" c="red">
                          {job.error}
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{job.queue}</Text>
                      <Text size="xs" c="dimmed">
                        {job.cron}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {job.lastJobState ? (
                        <Badge color={recurringStateColors[job.lastJobState]} variant="light">
                          {job.lastJobState}
                        </Badge>
                      ) : (
                        <Text size="sm" c="dimmed">
                          No history
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{formatDateTime(job.lastExecutionAt)}</Text>
                      <Text size="xs" c="dimmed">
                        Next {formatDateTime(job.nextExecutionAt)}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        ) : null}
        {diagnostics.recurringJobsTruncated && <TruncatedNotice subject="recurring jobs" />}
      </Stack>

      <Stack gap="sm">
        <Title order={4} size="h5">
          Queue backlogs
        </Title>
        {diagnostics.queueBacklogs.length === 0 && diagnostics.availability === 'Available' ? (
          <EmptyFact>No queued or fetched work was reported.</EmptyFact>
        ) : diagnostics.queueBacklogs.length > 0 ? (
          <Table.ScrollContainer minWidth={460}>
            <Table verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Queue</Table.Th>
                  <Table.Th ta="right">Enqueued</Table.Th>
                  <Table.Th ta="right">Fetched</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {diagnostics.queueBacklogs.map((queue) => (
                  <Table.Tr key={queue.queue}>
                    <Table.Td>{queue.queue}</Table.Td>
                    <Table.Td ta="right">{formatCount(queue.enqueuedCount)}</Table.Td>
                    <Table.Td ta="right">{formatCount(queue.fetchedCount)}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        ) : null}
      </Stack>
    </DiagnosticsSection>
  );
}

function StorageSection({ diagnostics }: { diagnostics: StorageDiagnosticsDto }) {
  return (
    <DiagnosticsSection
      title="Storage dependencies"
      description="Availability of the configured data volume and content-addressable storage root."
      error={diagnostics.error}
    >
      {(diagnostics.dataVolumeAvailability === 'Unavailable' ||
        diagnostics.casAvailability === 'Unavailable') && (
        <Alert color="red" icon={<IconAlertCircle size={16} />} title="Storage probe unavailable">
          {diagnostics.error || 'One or more storage probes did not complete.'}
        </Alert>
      )}
      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <Paper withBorder p="md" radius="sm">
          <Group justify="space-between" align="flex-start">
            <Box>
              <Text fw={600}>Data volume</Text>
              <Text size="sm" c="dimmed">
                {formatByteCount(diagnostics.dataVolumeFreeBytes)} free
              </Text>
            </Box>
            <AvailabilityBadge availability={diagnostics.dataVolumeAvailability} />
          </Group>
        </Paper>
        <Paper withBorder p="md" radius="sm">
          <Group justify="space-between" align="flex-start">
            <Box>
              <Text fw={600}>Content-addressable storage</Text>
              <Text size="sm" c="dimmed">
                Read/write root
              </Text>
            </Box>
            <AvailabilityBadge availability={diagnostics.casAvailability} />
          </Group>
        </Paper>
      </SimpleGrid>
    </DiagnosticsSection>
  );
}

function DiagnosticsSkeleton() {
  return (
    <Stack gap="lg" role="status" aria-label="Loading operational diagnostics">
      {[0, 1, 2, 3, 4].map((section) => (
        <Paper key={section} className={workspace.panel}>
          <Stack gap="md">
            <Skeleton height={24} width="35%" />
            <Skeleton height={14} width="70%" />
            <Skeleton height={96} />
          </Stack>
        </Paper>
      ))}
    </Stack>
  );
}

export function Diagnostics() {
  const query = useOperationalDiagnostics();

  return (
    <Stack className={workspace.page} gap="lg">
      <Group className={workspace.hero} justify="space-between" align="flex-start" wrap="wrap">
        <Box>
          <Title order={1} className={workspace.heading}>Diagnostics</Title>
          <Text c="dimmed" size="sm">
            Recovery, workers, catalog processing, and storage availability.
          </Text>
          {query.data && (
            <Text c="dimmed" size="xs" mt={4}>
              Snapshot generated {formatDateTime(query.data.generatedAt)}
            </Text>
          )}
        </Box>
        <Button
          {...workspaceActionProps}
          variant="light"
          leftSection={<IconRefresh size={16} />}
          onClick={() => void query.refetch()}
          loading={query.isFetching}
          disabled={query.isLoading}
        >
          Refresh snapshot
        </Button>
      </Group>

      {query.isLoading ? (
        <DiagnosticsSkeleton />
      ) : !query.data ? (
        <Alert
          color="red"
          icon={<IconAlertCircle size={18} />}
          title="Failed to load operational diagnostics"
        >
          <Stack gap="sm" align="flex-start">
            <Text size="sm">
              The bounded snapshot could not be loaded. Existing readiness probes remain separate from this page.
            </Text>
            <Button size="xs" variant="light" color="red" onClick={() => void query.refetch()}>
              Try again
            </Button>
          </Stack>
        </Alert>
      ) : (
        <>
          {query.isError && <Alert color="orange" title="Could not refresh diagnostics">Showing the previous snapshot. Refresh to try again.</Alert>}
          <OperationalAttention snapshot={query.data} />
          <CatalogProjectionsSection diagnostics={query.data.catalogProjections} />
          <JobsSection diagnostics={query.data.jobs} />
          <OutboxSection diagnostics={query.data.outbox} />
          <HangfireSection diagnostics={query.data.hangfire} />
          <StorageSection diagnostics={query.data.storage} />
        </>
      )}
    </Stack>
  );
}
