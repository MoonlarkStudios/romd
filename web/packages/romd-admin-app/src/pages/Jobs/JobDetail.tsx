import { Alert, Anchor, Badge, Button, Code, Group, Skeleton, Stack, Table, Text, Title } from '@mantine/core';
import { IconArrowLeft } from '@tabler/icons-react';
import { Link, useLocation, useParams } from 'react-router';
import { JobRow } from '../../components/Jobs/JobRows';
import { getJobTitle } from '../../components/Jobs/jobTypeRegistry';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useJob } from '../../hooks/api/useJobs';
import { useJobOperations } from '../../hooks/useJobOperations';
import { usePermissions } from '../../hooks/usePermissions';

export function JobDetail() {
  const { jobId } = useParams();
  const location = useLocation();
  const returnUrl = typeof location.state?.historyUrl === "string" && location.state.historyUrl.startsWith("/jobs?") ? location.state.historyUrl : "/jobs";
  const query = useJob(jobId);
  const operations = useJobOperations();
  const { hasRole } = usePermissions();
  const job = query.data;
  return <Stack className={workspace.page} gap="lg">
    <Button component={Link} to={returnUrl} variant="subtle" color="gray" p={0} leftSection={<IconArrowLeft size={15} />} style={{ alignSelf: 'flex-start' }}>Job history</Button>
    <Group className={workspace.hero} justify="space-between"><Title order={1} className={workspace.heading}>{job ? getJobTitle(job) : 'Job details'}</Title><Button variant="light" color="teal" loading={query.isFetching} onClick={() => void query.refetch()}>Refresh</Button></Group>
    {query.isError && <Alert color="red" title="Could not load job">The job may be unavailable, outside your access, or removed by retention. Refresh to try again.{job && ' Showing the last available details.'}</Alert>}
    {query.isPending ? <Skeleton h={180} /> : job && <>
      {job.isArchived && <Alert color="gray" title="Archived job">This record is retained until scheduled cleanup removes it, 30 days after archival.</Alert>}
      <JobRow job={job} {...operations} />
      <Stack className={workspace.panel} gap="sm">
        <Title order={2} className={workspace.sectionHeading}>Execution details</Title>
        <Group><Text size="sm">Phase</Text><Badge variant="light">{job.phase}</Badge></Group>
        <Text size="sm">Created {job.createdAt ? new Date(job.createdAt).toLocaleString() : 'Unknown'}</Text>
        {job.startedAt && <Text size="sm">Started {new Date(job.startedAt).toLocaleString()}</Text>}
        {job.completedAt && <Text size="sm">Finished {new Date(job.completedAt).toLocaleString()}</Text>}
        {job.currentItem && <Text size="sm" style={{ overflowWrap: 'anywhere' }}>Current item: {job.currentItem}</Text>}
        <Text size="sm">Job ID <Code>{job.id}</Code></Text><Text size="sm">Correlation ID <Code>{job.correlationId}</Code></Text>
        {job.jobType === 'replace_dat' && <Text size="sm">Previous DAT <Code>{job.existingDatId}</Code>{job.newDatId && <> · Replacement DAT <Code>{job.newDatId}</Code></>}</Text>}
        <Group>
          {job.jobType === 'replace_dat' && !job.systemKey && <Anchor component={Link} to="/systems">Find source in Systems</Anchor>}
          {job.systemKey && <Anchor component={Link} to={`/systems/${job.systemKey}?tab=sources`}>Open system sources</Anchor>}
          {(job.jobType === 'enrichment' || job.jobType === 'artwork-import') && <Anchor component={Link} to={`/titles/${job.titleId}`}>Open title</Anchor>}
          {(job.jobType === 'materialization' || job.jobType === 'export') && job.libraryId && hasRole('Admin') && <Anchor component={Link} to={`/libraries/${job.libraryId}`}>Open library</Anchor>}
        </Group>
        {job.phase === 'Deferred' && <Alert color="gray">The catalog must finish rebuilding before this library can update. Library processing retries automatically when the catalog is ready.</Alert>}
      </Stack>
      <Stack className={workspace.panel}><Title order={2} className={workspace.sectionHeading}>Errors</Title>
        {job.errors?.length ? <Table.ScrollContainer minWidth={600}><Table><Table.Thead><Table.Tr><Table.Th>Item</Table.Th><Table.Th>Error</Table.Th><Table.Th>Time</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{job.errors.map((error, index) => <Table.Tr key={`${error.occurredAt}-${index}`}><Table.Td style={{ overflowWrap: 'anywhere' }}>{error.item}</Table.Td><Table.Td style={{ overflowWrap: 'anywhere' }}>{error.message}</Table.Td><Table.Td>{new Date(error.occurredAt).toLocaleString()}</Table.Td></Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer> : <Text size="sm" c="dimmed">No errors recorded.</Text>}
      </Stack>
    </>}
  </Stack>;
}
