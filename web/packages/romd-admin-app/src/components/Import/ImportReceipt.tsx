import { ActionIcon, Alert, Button, Group, Stack, Text, Title, Tooltip } from '@mantine/core';
import type { JobUploadJob } from '@romd/admin-api-client';
import { IconX } from '@tabler/icons-react';
import { Link } from 'react-router';
import { useServerImportStatus } from '../../hooks/api/useServerImport';
import { usePermissions } from '../../hooks/usePermissions';
import { JobStatusBadge } from '../Jobs/JobStatusBadge';
import workspace from '../Workspace/Workspace.module.css';
import {
  importHistoryUrl,
  importReceiptUrl,
  serverImportUrl,
  uploadImportUrl,
} from './importReceiptLinks';
import { ProvenanceLedger } from './ProvenanceLedger';

export function ImportReceipt({ job, onDismiss }: { job: JobUploadJob; onDismiss?: () => void }) {
  const { canManageTitles } = usePermissions();
  const source = job.importSourcePath;
  const { data: server } = useServerImportStatus(!!source && canManageTitles, false);
  const serverFolder = canManageTitles && !!source && source === server?.directory;
  const latestServerBatch = serverFolder && server?.lastJobId === job.id;
  const retryUrl =
    latestServerBatch && server?.canRetry ? serverImportUrl : source ? undefined : uploadImportUrl;
  const cancelled = job.phase === 'Cancelled';
  const failed = job.phase === 'Failed';
  const stopped = cancelled || failed;
  const needsRecovery = stopped || job.phase === 'CompletedWithErrors';
  const stored = job.romsIngested ?? 0;
  const remaining =
    Math.max(0, (job.romsDiscovered ?? 0) - (job.romsProcessed ?? 0)) +
    Math.max(0, (job.datsDiscovered ?? 0) - (job.datsProcessed ?? 0));
  const reason = stopped ? job.errors.find((error) => error.item === 'job') : undefined;
  const errors = job.errors.filter((error) => error !== reason);
  const skipped = job.romsRejected ?? 0;
  const catalogs = job.datsSucceeded ?? 0;
  return (
    <section
      className={workspace.panel}
      id={onDismiss ? undefined : 'import-results'}
      aria-label="Import results"
    >
      <Stack gap="md">
        <Group
          justify="space-between"
          align="flex-start"
        >
          <Stack
            gap="xs"
            style={{
              minWidth: 0,
              flex: '1 1 300px',
            }}
          >
            <Title
              order={2}
              className={workspace.sectionHeading}
              style={{
                overflowWrap: 'anywhere',
              }}
            >
              {onDismiss ? job.sourceFilename : 'Import results'}
            </Title>
            <Group gap="md">
              <Text size="sm">{stored.toLocaleString()} stored</Text>
              <Text
                size="sm"
                c="dimmed"
              >
                {(job.romsDeduplicated ?? 0).toLocaleString()} already stored
              </Text>
              {skipped > 0 && (
                <Text
                  size="sm"
                  c="orange"
                >
                  {skipped.toLocaleString()} {job.trackedOnly ? 'skipped' : 'not stored'}
                </Text>
              )}
              {catalogs > 0 && (
                <Text size="sm">
                  {catalogs.toLocaleString()} {catalogs === 1 ? 'catalog' : 'catalogs'}
                </Text>
              )}
              {onDismiss && job.duration && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {job.duration}
                </Text>
              )}
            </Group>
          </Stack>
          {onDismiss && (
            <Group gap="xs">
              <JobStatusBadge job={job} />
              <Button
                component={Link}
                variant="subtle"
                size="xs"
                to={importReceiptUrl(job.id)}
                state={{
                  historyUrl: serverFolder ? serverImportUrl : importHistoryUrl,
                }}
              >
                Open import results
              </Button>
              <Tooltip label="Dismiss results">
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  aria-label="Dismiss results"
                  onClick={onDismiss}
                >
                  <IconX size={16} />
                </ActionIcon>
              </Tooltip>
            </Group>
          )}
        </Group>
        {skipped > 0 &&
          (job.trackedOnly ? (
            <Alert
              color="blue"
              title="Tracked-title filter applied"
            >
              ROMs outside the tracked selection were skipped. Track the missing titles before
              importing those files again.
            </Alert>
          ) : (
            <Alert
              color="orange"
              title="Unmatched files were not stored"
            >
              Check the matching DAT before importing those files again.
            </Alert>
          ))}
        {needsRecovery && (
          <Alert
            color={failed ? 'red' : cancelled ? 'gray' : 'orange'}
            title={
              cancelled ? 'Import stopped' : failed ? 'Import failed' : 'Import needs attention'
            }
          >
            <Stack gap="xs">
              {reason && <Text size="sm">{reason.message}</Text>}
              {stopped && (stored > 0 || catalogs > 0) && (
                <Text size="sm">Files and catalogs saved before the import stopped are kept.</Text>
              )}
              {remaining > 0 && (
                <Text size="sm">
                  {remaining.toLocaleString()} {remaining === 1 ? 'file was' : 'files were'} left
                  unprocessed.
                </Text>
              )}
              {source && (
                <Text
                  size="sm"
                  style={{
                    overflowWrap: 'anywhere',
                  }}
                >
                  Check the source folder for files still awaiting import. <strong>{source}</strong>
                </Text>
              )}
              {serverFolder && !latestServerBatch && (
                <Text size="sm">
                  Retry last import applies to the latest batch. To process files from this batch
                  again, move the remaining originals into a new subfolder or choose them in Upload.
                </Text>
              )}
              {!source && onDismiss && (
                <Text size="sm">
                  Choose the originals again in Upload after checking the results.
                </Text>
              )}
              {retryUrl && !onDismiss && (
                <Button
                  component={Link}
                  to={retryUrl}
                  variant="light"
                  size="compact-sm"
                  w="fit-content"
                >
                  {serverFolder ? 'Open server import folder' : 'Back to Upload'}
                </Button>
              )}
            </Stack>
          </Alert>
        )}
        {errors.length > 0 && (
          <Alert
            color="red"
            title="Processing errors"
          >
            <Stack gap={4}>
              {errors.slice(0, 5).map((error, index) => (
                <Text
                  key={`${error.item}-${index}`}
                  size="sm"
                  style={{
                    overflowWrap: 'anywhere',
                  }}
                >
                  {error.item}: {error.message}
                </Text>
              ))}
            </Stack>
          </Alert>
        )}
        <ProvenanceLedger
          jobId={job.id}
          addressable={!onDismiss}
          retryUrl={retryUrl ?? null}
        />
      </Stack>
    </section>
  );
}
