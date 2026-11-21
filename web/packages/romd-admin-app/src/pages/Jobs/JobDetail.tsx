import {
  Alert,
  Anchor,
  Badge,
  Button,
  Code,
  Group,
  Skeleton,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { IconArrowLeft } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useLocation, useParams } from 'react-router';
import { RunPanel } from '../../components/Enrichment/RunPanel';
import { enrichmentRunKeys } from '../../components/Enrichment/useEnrichmentRun';
import { ImportReceipt } from '../../components/Import/ImportReceipt';
import { importHistoryUrl, serverImportUrl } from '../../components/Import/importReceiptLinks';
import { JobRow } from '../../components/Jobs/JobRows';
import { getJobTitle, mediaReconciliationOutcome } from '../../components/Jobs/jobTypeRegistry';
import { reviewReturnPath } from '../../components/Title/reviewHandoff';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { jobItemsKey } from '../../hooks/api/useJobItems';
import { useJob } from '../../hooks/api/useJobs';
import { useJobOperations } from '../../hooks/useJobOperations';
import { usePermissions } from '../../hooks/usePermissions';

export function JobDetail() {
  const { jobId } = useParams();
  const location = useLocation();
  const query = useJob(jobId);
  const cache = useQueryClient();
  const operations = useJobOperations();
  const { hasRole } = usePermissions();
  const job = query.data;
  const fromServerImport = location.state?.historyUrl === serverImportUrl;
  const returnUrl = fromServerImport
    ? serverImportUrl
    : typeof location.state?.historyUrl === 'string' &&
        location.state.historyUrl.startsWith('/jobs?')
      ? location.state.historyUrl
      : job?.jobType === 'upload'
        ? importHistoryUrl
        : '/jobs';
  const run = job?.jobType === 'artwork-completion' && hasRole('Manager') ? job : undefined;
  const returnToReview = hasRole('Manager') && reviewReturnPath(location.state?.reviewReturnTo);
  const refresh = () => {
    void query.refetch();
    if (job?.jobType === 'upload' && job.isTerminal)
      void cache.invalidateQueries({
        queryKey: jobItemsKey(job.id),
      });
    if (run)
      void cache.invalidateQueries({
        queryKey: enrichmentRunKeys.run(run.id),
      });
  };
  return (
    <Stack
      className={workspace.page}
      gap="lg"
    >
      <Group gap="md">
        <Button
          component={Link}
          to={returnUrl}
          variant="subtle"
          color="gray"
          p={0}
          leftSection={<IconArrowLeft size={15} />}
          style={{
            alignSelf: 'flex-start',
          }}
        >
          {fromServerImport ? 'Server import folder' : 'Job history'}
        </Button>
        {returnToReview && (
          <Button
            component={Link}
            to={returnToReview}
            variant="light"
            size="compact-sm"
          >
            Back to review
          </Button>
        )}
      </Group>
      <Group
        className={workspace.hero}
        justify="space-between"
      >
        <Title
          order={1}
          className={workspace.heading}
        >
          {job ? getJobTitle(job) : 'Job details'}
        </Title>
        <Button
          {...workspaceActionProps}
          variant="light"
          loading={query.isFetching}
          onClick={refresh}
        >
          Refresh
        </Button>
      </Group>
      {query.isError && (
        <Alert
          color="red"
          title="Could not load job"
        >
          The job may be unavailable, outside your access, or removed by retention. Refresh to try
          again.{job && ' Showing the last available details.'}
        </Alert>
      )}
      {query.isPending ? (
        <Skeleton h={180} />
      ) : (
        job && (
          <>
            {job.isArchived && (
              <Alert
                color="gray"
                title="Archived job"
              >
                This record is retained until scheduled cleanup removes it, 30 days after archival.
              </Alert>
            )}
            <JobRow
              job={job}
              showFiles={false}
              {...operations}
            />
            {job.jobType === 'upload' && job.isTerminal && (
              <ImportReceipt
                key={job.id}
                job={job}
              />
            )}
            {run && (
              <RunPanel
                job={run}
                key={run.id}
              />
            )}
            <Stack
              className={workspace.panel}
              gap="sm"
            >
              <Title
                order={2}
                className={workspace.sectionHeading}
              >
                Execution details
              </Title>
              <Group>
                <Text size="sm">Phase</Text>
                <Badge variant="light">{job.phase}</Badge>
              </Group>
              <Text size="sm">
                Created {job.createdAt ? new Date(job.createdAt).toLocaleString() : 'Unknown'}
              </Text>
              {job.startedAt && (
                <Text size="sm">Started {new Date(job.startedAt).toLocaleString()}</Text>
              )}
              {job.completedAt && (
                <Text size="sm">Finished {new Date(job.completedAt).toLocaleString()}</Text>
              )}
              {!job.isTerminal && job.nextAttemptAt && (
                <Text size="sm">Next attempt {new Date(job.nextAttemptAt).toLocaleString()}</Text>
              )}
              {job.jobType === 'media-reconciliation' && (
                <Stack gap={4}>
                  {job.subject && (
                    <Text size="sm">
                      Subject: {job.subject.kind} · {job.subject.id}
                    </Text>
                  )}
                  {job.originSelectionJobId && (
                    <Anchor
                      component={Link}
                      to={`/jobs/${job.originSelectionJobId}`}
                    >
                      Original selection
                    </Anchor>
                  )}
                  {job.outcome && (
                    <Text size="sm">Outcome: {mediaReconciliationOutcome(job.outcome)}</Text>
                  )}
                  {job.outcomeCode && (
                    <Text size="sm">
                      Reason: <Code>{job.outcomeCode}</Code>
                    </Text>
                  )}
                  {job.selection && (
                    <>
                      <Text size="sm">
                        Scope:{' '}
                        {job.selection.filter === 'RetainedRepresentations'
                          ? 'Retained representations'
                          : job.selection.filter === 'KindSubjects'
                            ? job.selection.kindId
                            : 'All media'}
                      </Text>
                      <Text size="sm">
                        {job.selection.enumerationComplete
                          ? 'Selection complete'
                          : 'Selecting media'}{' '}
                        · Maximum {job.selection.limit}
                      </Text>
                      <Text size="sm">
                        Selected {job.selection.selected} · Created {job.selection.created} ·
                        Adopted {job.selection.adopted}
                      </Text>
                      <Text size="sm">
                        Finished {job.selection.completed} · Failed {job.selection.failed} · Skipped{' '}
                        {job.selection.skipped}
                      </Text>
                      <Text size="sm">
                        Requested conversion: {job.selection.conversionRequested ? 'Yes' : 'No'}
                      </Text>
                    </>
                  )}
                </Stack>
              )}
              {job.currentItem && (
                <Text
                  size="sm"
                  style={{
                    overflowWrap: 'anywhere',
                  }}
                >
                  Current item: {job.currentItem}
                </Text>
              )}
              <Text size="sm">
                Job ID <Code>{job.id}</Code>
              </Text>
              <Text size="sm">
                Correlation ID <Code>{job.correlationId}</Code>
              </Text>
              {job.jobType === 'replace_dat' && (
                <Text size="sm">
                  Previous DAT <Code>{job.existingDatId}</Code>
                  {job.newDatId && (
                    <>
                      {' '}
                      · Replacement DAT <Code>{job.newDatId}</Code>
                    </>
                  )}
                </Text>
              )}
              <Group>
                {job.jobType === 'media-reconciliation' && (
                  <Anchor
                    component={Link}
                    to="/storage"
                  >
                    Open media storage
                  </Anchor>
                )}
                {job.jobType === 'media-reconciliation' &&
                  job.subject?.kind === 'OriginalLayout' && (
                    <Anchor
                      component={Link}
                      to="/disc-layouts"
                    >
                      Open disc layouts
                    </Anchor>
                  )}
                {job.jobType === 'replace_dat' && !job.systemKey && (
                  <Anchor
                    component={Link}
                    to="/systems"
                  >
                    Find source in Systems
                  </Anchor>
                )}
                {job.systemKey && (
                  <Anchor
                    component={Link}
                    to={`/systems/${job.systemKey}?tab=sources`}
                  >
                    Open system sources
                  </Anchor>
                )}
                {(job.jobType === 'enrichment' || job.jobType === 'artwork-import') && (
                  <Anchor
                    component={Link}
                    to={`/titles/${job.titleId}`}
                  >
                    Open title
                  </Anchor>
                )}
                {(job.jobType === 'materialization' || job.jobType === 'export') &&
                  job.libraryId &&
                  hasRole('Admin') && (
                    <Anchor
                      component={Link}
                      to={`/libraries/${job.libraryId}`}
                    >
                      Open library
                    </Anchor>
                  )}
              </Group>
              {job.phase === 'Deferred' && job.jobType === 'materialization' && (
                <Alert color="gray">
                  The catalog must finish rebuilding before this library can update. Library
                  processing retries automatically when the catalog is ready.
                </Alert>
              )}
            </Stack>
            <Stack className={workspace.panel}>
              <Title
                order={2}
                className={workspace.sectionHeading}
              >
                Errors
              </Title>
              {job.errors?.length ? (
                <Table.ScrollContainer minWidth={600}>
                  <Table>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>Item</Table.Th>
                        <Table.Th>Error</Table.Th>
                        <Table.Th>Time</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {job.errors.map((error, index) => (
                        <Table.Tr key={`${error.occurredAt}-${index}`}>
                          <Table.Td
                            style={{
                              overflowWrap: 'anywhere',
                            }}
                          >
                            {error.item}
                          </Table.Td>
                          <Table.Td
                            style={{
                              overflowWrap: 'anywhere',
                            }}
                          >
                            {error.message}
                          </Table.Td>
                          <Table.Td>{new Date(error.occurredAt).toLocaleString()}</Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
              ) : (
                <Text
                  size="sm"
                  c="dimmed"
                >
                  No errors recorded.
                </Text>
              )}
            </Stack>
          </>
        )
      )}
    </Stack>
  );
}
