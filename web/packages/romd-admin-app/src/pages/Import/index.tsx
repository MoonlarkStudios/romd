import { Alert, Button, Divider, Group, Select, Stack, Title } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { ImportIntake } from './ImportIntake';
import { ImportJobCard } from './ImportJobCard';
import { RecentImports } from './RecentImports';
import { useImportSession } from './useImportSession';

export function ImportPage({ embedded = false }: { embedded?: boolean }) {
  const session = useImportSession();
  const [addingFiles, setAddingFiles] = useState(false);
  useEffect(() => {
    if (session.isStarting) setAddingFiles(false);
  }, [
    session.isStarting,
  ]);
  const showStage =
    addingFiles ||
    session.staged.length > 0 ||
    session.isStarting ||
    session.activeJobIds.length === 0;
  return (
    <Stack gap="xl">
      {!embedded && (
        <Title
          order={1}
          className={workspace.heading}
        >
          Import ROMs
        </Title>
      )}
      {showStage ? (
        <ImportIntake
          session={session}
          onStarted={() => setAddingFiles(false)}
        />
      ) : (
        <Group justify="flex-end">
          <Button
            variant="default"
            leftSection={<IconPlus size={16} />}
            onClick={() => setAddingFiles(true)}
          >
            Import more files
          </Button>
        </Group>
      )}
      {session.batchError && (
        <Alert
          color="red"
          title={
            session.batchError === 'next-page'
              ? 'More imports could not be loaded'
              : 'Import could not be restored'
          }
        >
          <Button
            variant="subtle"
            loading={session.fetchingMoreJobs}
            onClick={session.retryBatch}
          >
            Retry loading imports
          </Button>
        </Alert>
      )}
      {(session.activeJobIds.length > 1 || session.hasMoreJobs) && (
        <Group align="flex-end">
          <Select
            label="Import to review"
            searchable
            allowDeselect={false}
            value={session.selectedJobId}
            onChange={(id) => {
              if (id) session.reopenJob(id);
            }}
            data={session.activeJobIds.map((id, index) => ({
              value: id,
              label: `${index + 1}. ${session.jobNames.get(id) ?? 'Import'}`,
            }))}
            style={{
              flex: 1,
            }}
          />
          {session.hasMoreJobs && session.batchError !== 'next-page' && (
            <Button
              variant="default"
              loading={session.fetchingMoreJobs}
              onClick={session.loadMoreJobs}
            >
              Load more imports
            </Button>
          )}
        </Group>
      )}
      {session.selectedJobId && (
        <ImportJobCard
          key={session.selectedJobId}
          jobId={session.selectedJobId}
          onDismiss={() => {
            if (session.selectedJobId) session.dismissJob(session.selectedJobId);
          }}
        />
      )}
      <Divider />
      <RecentImports
        activeJobIds={session.activeJobIds}
        onReopen={session.reopenJob}
      />
    </Stack>
  );
}
