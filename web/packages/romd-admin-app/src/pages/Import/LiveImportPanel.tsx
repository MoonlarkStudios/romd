import { Alert, Badge, Button, Group, Modal, Progress, Stack, Text, Title } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import type { JobDtoUploadJobDto } from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useCancelJob } from '../../hooks/api/useJobActions';
import { formatDuration } from '../../utils/format';

const PHASE_LABELS: Record<string, string> = {
  Pending: 'Queued', Extracting: 'Extracting archives', Classifying: 'Discovering files',
  IngestingDats: 'Importing DAT catalogs', IngestingRoms: 'Processing ROMs',
};

export function LiveImportPanel({ job }: { job: JobDtoUploadJobDto }) {
  const cancel = useCancelJob();
  const [confirm, { open, close }] = useDisclosure(false);
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(timer); }, []);
  const total = Number(job.phase === 'IngestingDats' ? job.datsDiscovered : job.romsDiscovered);
  const processed = Number(job.phase === 'IngestingDats' ? job.datsProcessed : job.romsProcessed);
  const measurable = ['IngestingDats', 'IngestingRoms'].includes(job.phase) && total > 0;
  const elapsed = job.startedAt ? Math.max(0, (now - new Date(job.startedAt).getTime()) / 1000) : null;
  return <section><Stack gap="md">
    <Group justify="space-between" align="flex-start"><Stack gap={4} style={{ minWidth: 0, flex: '1 1 300px' }}>
      <Title order={2} className={workspace.sectionHeading} style={{ overflowWrap: 'anywhere' }}>{job.sourceFilename}</Title>
      <Text size="sm" c="dimmed">{PHASE_LABELS[job.phase] ?? job.phase}{elapsed !== null ? ` · ${formatDuration(elapsed)} elapsed` : ''}</Text>
    </Stack><Badge color="blue" variant="light">{job.phase === 'Pending' ? 'Queued' : 'Running'}</Badge></Group>
    <Progress aria-label={measurable ? 'Files processed' : 'Processing activity'} value={measurable ? processed / total * 100 : 100} striped={!measurable} animated size="sm" color="teal" />
    <Group justify="space-between" align="flex-start">
      <Stack gap={4} style={{ minWidth: 0, flex: '1 1 280px' }}>
        <Text size="sm">{measurable ? `${processed.toLocaleString()} of ${total.toLocaleString()} files processed` : `${(Number(job.datsDiscovered) + Number(job.romsDiscovered)).toLocaleString()} files discovered`}</Text>
        {job.currentItem && <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{job.currentItem}</Text>}
      </Stack><Button variant="subtle" color="gray" onClick={open} disabled={cancel.isSuccess}>Cancel import</Button>
    </Group>
    {cancel.isSuccess && <Text size="sm" c="dimmed">Cancellation requested</Text>}
    <Modal opened={confirm} onClose={() => { if (!cancel.isPending) close(); }} title="Cancel import?" centered>
      <Stack><Text size="sm">Files already stored will remain. Unprocessed files will be discarded.</Text>
        {cancel.isError && <Alert color="red">Cancellation failed. Try again.</Alert>}
        <Group justify="flex-end"><Button variant="default" disabled={cancel.isPending} onClick={close}>Keep importing</Button><Button color="red" loading={cancel.isPending} onClick={() => cancel.mutate(job.id, { onSuccess: close })}>Cancel import</Button></Group>
      </Stack>
    </Modal>
  </Stack></section>;
}
