import { Alert, Anchor, Button, Code, Group, Stack, Text, Title } from '@mantine/core';
import type { ServerImportStatus } from '@romd/admin-api-client';
import { IconFolderUp } from '@tabler/icons-react';
import { Link } from 'react-router';
import { importReceiptUrl, serverImportUrl } from '../../components/Import/importReceiptLinks';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import type { useStartServerImport } from '../../hooks/api/useServerImport';

const formatCheckTime = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
  timeStyle: 'short',
});

const checkLabels: Record<string, string> = {
  NoNewFiles: 'No new files',
  WaitingForFiles: 'Waiting for files to finish arriving',
  Failed: 'Could not check the folder',
};

const importLabels: Record<string, string> = {
  Completed: 'Last import completed',
  CompletedWithErrors: 'Last import completed with errors',
  Failed: 'Last import failed',
  Cancelled: 'Last import stopped',
};

export function ServerImportFolder({
  status,
  start,
  onStarted,
}: {
  status: ServerImportStatus;
  start: ReturnType<typeof useStartServerImport>;
  onStarted: (jobId: string, name?: string) => void;
}) {
  const check = (retryJobId?: string) => {
    start.mutate(retryJobId, {
      onSuccess: (result) => {
        if ('jobId' in result) {
          const name = status.directory?.split(/[\\/]/).filter(Boolean).at(-1);
          onStarted(result.jobId, name);
        }
      },
    });
  };
  const lastChecked = status.lastCheckedAt
    ? formatCheckTime.format(new Date(status.lastCheckedAt))
    : null;
  const nextCheck = status.nextCheckAt
    ? formatCheckTime.format(new Date(status.nextCheckAt))
    : null;
  const lastJob = status.lastJobId;
  const readyToRetry = lastJob && status.canRetry;
  const checkLabel =
    status.lastCheckOutcome === 'Queued'
      ? `Queued ${status.lastQueuedFiles.toLocaleString()} ${status.lastQueuedFiles === 1 ? 'file' : 'files'}`
      : status.lastCheckOutcome
        ? (checkLabels[status.lastCheckOutcome] ?? 'Folder checked')
        : 'Ready to check';
  const lastImport =
    status.lastJobPhase === 'Completed' && status.canRetry
      ? 'Last import has files to review'
      : status.lastJobPhase && importLabels[status.lastJobPhase];

  return (
    <Stack gap="md">
      <Stack gap={4}>
        <Title
          order={2}
          className={workspace.sectionHeading}
        >
          Server import folder
        </Title>
        <Code
          style={{
            alignSelf: 'flex-start',
            overflowWrap: 'anywhere',
          }}
        >
          {status.directory}
        </Code>
        <Text
          size="sm"
          c="dimmed"
        >
          ROMD removes files from this folder after storing them. Files that need review stay here.
        </Text>
      </Stack>
      <Stack
        gap={4}
        role="status"
        aria-live="polite"
      >
        <Text size="sm">
          {status.isRunning ? 'Import in progress' : checkLabel}
          {lastChecked && (
            <Text
              span
              c="dimmed"
            >
              {' '}
              · Last checked {lastChecked}
            </Text>
          )}
        </Text>
        {status.waitingFiles > 0 && (
          <Text
            size="sm"
            c="dimmed"
          >
            {status.waitingFiles === 1
              ? '1 file is'
              : `${status.waitingFiles.toLocaleString()} files are`}{' '}
            still waiting to finish.
          </Text>
        )}
      </Stack>
      <Text
        size="sm"
        c="dimmed"
      >
        {status.intervalMinutes
          ? `Checks every ${status.intervalMinutes} minutes${nextCheck ? ` · Next scheduled check ${nextCheck}` : ''}`
          : 'Automatic checks are off'}
      </Text>
      {(status.message || start.error?.message) && (
        <Alert color={start.isError || status.lastCheckOutcome === 'Failed' ? 'red' : 'orange'}>
          {start.error?.message ?? status.message}
        </Alert>
      )}
      <Group gap="sm">
        {status.isRunning && lastJob ? (
          <Button
            component={Link}
            to={importReceiptUrl(lastJob)}
            state={{
              historyUrl: serverImportUrl,
            }}
            {...workspaceActionProps}
          >
            View running import
          </Button>
        ) : (
          <Button
            {...workspaceActionProps}
            leftSection={<IconFolderUp size={16} />}
            loading={start.isPending && !start.variables}
            disabled={start.isPending}
            onClick={() => check()}
          >
            Import from server folder
          </Button>
        )}
        {readyToRetry && (
          <Button
            {...workspaceActionProps}
            variant="default"
            loading={start.isPending && !!start.variables}
            disabled={start.isPending}
            onClick={() => check(lastJob)}
          >
            Retry last import
          </Button>
        )}
        {!status.isRunning && lastJob && (
          <Anchor
            component={Link}
            to={importReceiptUrl(lastJob)}
            state={{
              historyUrl: serverImportUrl,
            }}
            size="sm"
          >
            View last import
          </Anchor>
        )}
      </Group>
      {lastImport && (
        <Text
          size="sm"
          c={status.canRetry ? 'orange' : 'dimmed'}
        >
          {lastImport}
        </Text>
      )}
    </Stack>
  );
}
