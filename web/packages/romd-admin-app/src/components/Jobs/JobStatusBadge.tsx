import { Badge } from '@mantine/core';
import type { JobDto } from '@romd/admin-api-client';
import { getJobOutcome, jobOutcomes } from './jobTypeRegistry';

export function JobStatusBadge({ job }: { job: JobDto }) {
  const status = jobOutcomes[getJobOutcome(job)];
  return <Badge color={status.color} size="sm" variant="light">{status.label}</Badge>;
}
