import { Alert, Button, Group, Loader, Text } from '@mantine/core';
import { useJob } from '../../hooks/api/useJobs';
import { LiveImportPanel } from './LiveImportPanel';
import { ResultsPanel } from './ResultsPanel';

/**
 * Tracks one import job and renders the live (Act 2) or results (Act 3) view.
 * The job is kept fresh by the SignalR socket writing into the React Query
 * cache, with polling as a fallback (see useJob).
 */
export function ImportJobCard({ jobId, onDismiss }: { jobId: string; onDismiss: () => void }) {
  const { data: job, isError, refetch } = useJob(jobId);
  if (isError) return <Alert color="red" title="Import status unavailable"><Button variant="subtle" onClick={() => void refetch()}>Retry status</Button></Alert>;

  if (!job) {
    return (
        <Group gap="sm">
          <Loader size="sm" />
          <Text size="sm" c="dimmed">
            Starting import…
          </Text>
        </Group>
    );
  }

  if (job.jobType !== 'upload') return null;

  return job.isTerminal ? (
    <ResultsPanel job={job} onDismiss={onDismiss} />
  ) : (
    <LiveImportPanel job={job} />
  );
}
