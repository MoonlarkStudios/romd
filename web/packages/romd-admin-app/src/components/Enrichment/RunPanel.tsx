import { Alert, Anchor, Badge, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import type { ArtworkCompletionItem } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { enrichmentWorkspaceCountsQuery } from '../../hooks/api/useEnrichmentWorkspace';
import { useDraftGuard } from '../Administration/DraftGuard';
import { TitleStatusChips } from '../Title/TitleStatusChips';
import workspace from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import {
  executionLine,
  globalReviewLabel,
  isRunActive,
  recordedLine,
  runCountsLine,
  runHeading,
} from './runPresentation';
import {
  type ArtworkRunJob,
  type RunRetryStatus,
  useEnrichmentRun,
  useEnrichmentRunItems,
  useRetryEnrichmentRun,
} from './useEnrichmentRun';

const completionLabels: Partial<Record<string, string>> = {
  Working: 'In progress',
  Unfinished: 'Unfinished',
};

function RunItem({ item }: { item: ArtworkCompletionItem }) {
  const search = new URLSearchParams({
    view: 'review',
    title: item.titleId,
  });
  return (
    <Stack
      gap={4}
      py="xs"
    >
      <Group gap="xs">
        <Anchor
          component={Link}
          to={`/catalog?${search}`}
          size="sm"
          fw={600}
        >
          {item.titleName}
        </Anchor>
        {completionLabels[item.completion] && (
          <Badge
            size="xs"
            color="gray"
          >
            {completionLabels[item.completion]}
          </Badge>
        )}
      </Group>
      <Text
        size="sm"
        c="dimmed"
      >
        {recordedLine(item)}
      </Text>
      <Group gap="xs">
        <Text
          size="sm"
          c="dimmed"
        >
          Now:
        </Text>
        {item.now ? (
          <TitleStatusChips
            flags={item.now}
            label={`${item.titleName} status now`}
          />
        ) : (
          <Text
            size="sm"
            c="dimmed"
          >
            not available
          </Text>
        )}
      </Group>
    </Stack>
  );
}

function ReviewList({ jobId }: { jobId: string }) {
  const items = useEnrichmentRunItems(jobId, 'Review');
  if (items.isPending) return <Skeleton height={60} />;
  if (items.isError)
    return (
      <Alert color="red">
        {items.error.message}
        <Button
          variant="subtle"
          onClick={() => void items.refetch()}
        >
          Retry
        </Button>
      </Alert>
    );
  const rows = items.data.pages.flatMap((page) => page.items);
  return (
    <Stack gap={0}>
      {rows.length ? (
        rows.map((item) => (
          <RunItem
            key={item.titleId}
            item={item}
          />
        ))
      ) : (
        <Text
          size="sm"
          c="dimmed"
        >
          No titles in this run need review now.
        </Text>
      )}
      {items.hasNextPage && (
        <Button
          variant="subtle"
          size="xs"
          w="fit-content"
          loading={items.isFetchingNextPage}
          onClick={() => void items.fetchNextPage()}
        >
          Show more
        </Button>
      )}
    </Stack>
  );
}

/** Execution history and current review needs of one run, reported on separate lines. */
export function RunPanel({ job }: { job: ArtworkRunJob }) {
  const jobId = job.id;
  const { state } = useLocation();
  const navigate = useNavigate();
  const run = useEnrichmentRun(job);
  // Reuse the Catalog queue counts and let the run's cheaper checks poll independently.
  const counts = useQuery({
    ...enrichmentWorkspaceCountsQuery(''),
    staleTime: 60_000,
  });
  const { discardThen } = useDraftGuard();
  const retry = useRetryEnrichmentRun(jobId);
  const [reviewOpen, setReviewOpen] = useState(false);
  const startRetry = (status: RunRetryStatus) =>
    void discardThen(async () => {
      const retryId = await retry.mutateAsync(status);
      navigate(`/jobs/${encodeURIComponent(retryId)}`, {
        state,
      });
    }).catch(() => undefined);
  const active = isRunActive(job.phase);

  return (
    <section
      className={workspace.panel}
      aria-label="Enrichment run"
    >
      <Stack gap="xs">
        <Title
          order={2}
          className={workspace.sectionHeading}
        >
          {runHeading({
            mode: job.mode,
            total: Number(job.total ?? 0),
          })}
        </Title>
        <Text size="sm">
          {executionLine({
            phase: job.phase,
            total: Number(job.total ?? 0),
            processed: Number(job.processed ?? 0),
            nextAttemptAt: job.nextAttemptAt ?? null,
          })}
        </Text>
        {run.isPending ? (
          <Skeleton height={40} />
        ) : run.isError ? (
          <Alert color="red">
            {run.error.message}
            <Button
              variant="subtle"
              onClick={() => void run.refetch()}
            >
              Retry
            </Button>
          </Alert>
        ) : (
          <>
            <Text
              size="sm"
              c="dimmed"
            >
              {runCountsLine(run.data)}
            </Text>
            <Group justify="space-between">
              <Group gap="xs">
                {run.data.review > 0 && (
                  <Button
                    {...workspaceActionProps}
                    size="xs"
                    variant="light"
                    aria-expanded={reviewOpen}
                    onClick={() => setReviewOpen(!reviewOpen)}
                  >
                    Review {run.data.review.toLocaleString()} in this run
                  </Button>
                )}
                {!active && run.data.failed > 0 && (
                  <Button
                    {...workspaceActionProps}
                    size="xs"
                    variant="light"
                    loading={retry.isPending && retry.variables === 'Failed'}
                    disabled={retry.isPending}
                    onClick={() => startRetry('Failed')}
                  >
                    Retry {run.data.failed.toLocaleString()} failed
                  </Button>
                )}
                {!active && run.data.unfinished > 0 && (
                  <Button
                    {...workspaceActionProps}
                    size="xs"
                    variant="light"
                    loading={retry.isPending && retry.variables === 'Unfinished'}
                    disabled={retry.isPending}
                    onClick={() => startRetry('Unfinished')}
                  >
                    Retry {run.data.unfinished.toLocaleString()} unfinished
                  </Button>
                )}
              </Group>
              <Anchor
                component={Link}
                to="/catalog?view=review"
                size="sm"
              >
                {globalReviewLabel(counts.data?.needsReview)}
              </Anchor>
            </Group>
            {retry.isError && <Alert color="red">{retry.error.message}</Alert>}
            {reviewOpen && run.data.review > 0 && <ReviewList jobId={jobId} />}
          </>
        )}
      </Stack>
    </section>
  );
}
