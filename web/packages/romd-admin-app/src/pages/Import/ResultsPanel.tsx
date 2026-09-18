import { ActionIcon, Alert, Badge, Group, Stack, Text, Title, Tooltip } from '@mantine/core';
import type { JobDtoUploadJobDto } from '@romd/admin-api-client';
import { IconX } from '@tabler/icons-react';
import { ProvenanceLedger } from '../../components/Import/ProvenanceLedger';
import workspace from '../../components/Workspace/Workspace.module.css';

export function ResultsPanel({ job, onDismiss }: { job: JobDtoUploadJobDto; onDismiss: () => void }) {
  const cancelled = job.phase === 'Cancelled';
  const failed = job.phase === 'Failed';
  const issues = job.hasErrors || Number(job.romsRejected) > 0;
  const label = cancelled ? 'Cancelled' : failed ? 'Failed' : issues ? 'Completed with issues' : 'Complete';
  return <section><Stack gap="md">
    <Group justify="space-between" align="flex-start"><Stack gap="xs" style={{ minWidth: 0, flex: '1 1 300px' }}>
      <Title order={2} className={workspace.sectionHeading} style={{ overflowWrap: 'anywhere' }}>{job.sourceFilename}</Title>
      <Group gap="md"><Text size="sm">{Number(job.romsIngested).toLocaleString()} stored</Text><Text size="sm" c="dimmed">{Number(job.romsDeduplicated).toLocaleString()} already stored</Text>
        {!!Number(job.romsRejected) && <Text size="sm" c="orange">{Number(job.romsRejected).toLocaleString()} not stored</Text>}
        {!!Number(job.datsSucceeded) && <Text size="sm">{Number(job.datsSucceeded).toLocaleString()} catalogs</Text>}
        {job.duration && <Text size="xs" c="dimmed">{job.duration}</Text>}
      </Group>
    </Stack><Group gap="xs"><Badge color={failed ? 'red' : cancelled ? 'gray' : issues ? 'orange' : 'teal'} variant="light">{label}</Badge>
      <Tooltip label="Dismiss results"><ActionIcon variant="subtle" color="gray" aria-label="Dismiss results" onClick={onDismiss}><IconX size={16} /></ActionIcon></Tooltip></Group></Group>
    {!!Number(job.romsRejected) && <Alert color="orange" title="Unmatched files were not stored">Import a matching DAT and upload those files again, or enable Keep unmatched files on your next import.</Alert>}
    {(cancelled || failed) && <Alert color={failed ? 'red' : 'gray'} title={cancelled ? 'Import stopped' : 'Import failed'}>Files already stored remain available. Unprocessed files were not imported.</Alert>}
    {job.errors.length > 0 && <Alert color="red" title="Processing errors"><Stack gap={4}>{job.errors.slice(0, 5).map((error, index) => <Text key={`${error.item}-${index}`} size="sm" style={{ overflowWrap: 'anywhere' }}>{error.item}: {error.message}</Text>)}</Stack></Alert>}
    <ProvenanceLedger jobId={job.id} />
  </Stack></section>;
}
