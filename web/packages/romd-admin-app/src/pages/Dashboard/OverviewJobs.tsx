import { Anchor, Progress, Table, Text } from '@mantine/core';
import type { JobDto } from '@romd/admin-api-client';
import { Link } from 'react-router';
import { JobStatusBadge } from '../../components/Jobs/JobStatusBadge';
import { getJobTitle, getJobTypeConfig } from '../../components/Jobs/jobTypeRegistry';
import { formatRelativeTime } from './utils';

export function OverviewJobs({ jobs }: { jobs: JobDto[] }) {
  return <Table.ScrollContainer minWidth={440}><Table verticalSpacing="sm"><Table.Thead><Table.Tr><Table.Th>Job</Table.Th><Table.Th>Status</Table.Th><Table.Th>Progress</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{jobs.map(job => <Table.Tr key={job.id}>
    <Table.Td><Anchor component={Link} to={`/jobs/${job.id}`} size="sm">{getJobTitle(job)}</Anchor><Text size="xs" c="dimmed">{getJobTypeConfig(job.jobType).label}{job.createdAt && ` · ${formatRelativeTime(job.createdAt)}`}</Text></Table.Td>
    <Table.Td><JobStatusBadge job={job} /></Table.Td><Table.Td>{!job.isTerminal ? <><Text size="xs">{Math.round(Number(job.progressPercent ?? 0))}%</Text><Progress aria-label="Job progress" value={Number(job.progressPercent ?? 0)} color="teal" size="xs" /></> : '—'}</Table.Td>
  </Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>;
}
