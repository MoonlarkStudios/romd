import { ActionIcon, Alert, Button, Group, Stack, Text, Title, Tooltip } from '@mantine/core';
import type { JobUploadJob } from '@romd/admin-api-client';
import { IconX } from '@tabler/icons-react';
import { Link } from 'react-router';
import { JobStatusBadge } from '../Jobs/JobStatusBadge';
import workspace from '../Workspace/Workspace.module.css';
import { importHistoryUrl, importReceiptUrl } from './importReceiptLinks';
import { ProvenanceLedger } from './ProvenanceLedger';

export function ImportReceipt({ job, onDismiss }: { job: JobUploadJob; onDismiss?: () => void }) {
  const cancelled = job.phase === 'Cancelled';
  const failed = job.phase === 'Failed';
  const skipped = Number(job.romsRejected);
  const catalogs = Number(job.datsSucceeded);
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
              <Text size="sm">{Number(job.romsIngested).toLocaleString()} stored</Text>
              <Text
                size="sm"
                c="dimmed"
              >
                {Number(job.romsDeduplicated).toLocaleString()} already stored
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
              {job.duration && (
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
                  historyUrl: importHistoryUrl,
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
              ROMs without a match to a currently tracked title were skipped. Track additional
              titles and upload again to keep them.
            </Alert>
          ) : (
            <Alert
              color="orange"
              title="Unmatched files were not stored"
            >
              Check the matching DAT before importing those files again.
            </Alert>
          ))}
        {(cancelled || failed) && (
          <Alert
            color={failed ? 'red' : 'gray'}
            title={cancelled ? 'Import stopped' : 'Import failed'}
          >
            Files already stored remain available. Unprocessed files were not imported.
          </Alert>
        )}
        {job.errors.length > 0 && (
          <Alert
            color="red"
            title="Processing errors"
          >
            <Stack gap={4}>
              {job.errors.slice(0, 5).map((error, index) => (
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
        />
      </Stack>
    </section>
  );
}
