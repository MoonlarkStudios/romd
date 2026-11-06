import {
  Alert,
  Anchor,
  Badge,
  Button,
  CloseButton,
  Group,
  Skeleton,
  Stack,
  Text,
  UnstyledButton,
} from '@mantine/core';
import type { ArtworkCompletionItemDto } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { invalidateEnrichmentWorkspace } from '../../hooks/api/enrichmentWorkspaceCache';
import { enrichmentWorkspaceCountsQuery } from '../../hooks/api/useEnrichmentWorkspace';
import { useCancelJob } from '../../hooks/api/useJobActions';
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
  type RunRetryStatus,
  useEnrichmentRun,
  useEnrichmentRunItems,
  useRetryEnrichmentRun,
} from './useEnrichmentRun';

export interface RunPanelProps {
  jobId: string;
  /** The host removes `activity` from the URL. */
  onDismiss: () => void;
  /** Opens the title in the workbench; the host keeps `activity`. */
  onOpenTitle: (titleId: string) => void;
  /** A retry queued a continuation run; the host points `activity` at it. */
  onRunChange: (jobId: string) => void;
}

const completionLabels: Partial<Record<string, string>> = {
  Working: 'In progress',
  Unfinished: 'Unfinished',
};

function RunItem({
  item,
  onOpenTitle,
}: {
  item: ArtworkCompletionItemDto;
  onOpenTitle: (titleId: string) => void;
}) {
  return (
    <Stack
      gap={4}
      py="xs"
    >
      <Group gap="xs">
        <UnstyledButton onClick={() => onOpenTitle(item.titleId)}>
          <Text
            size="sm"
            fw={600}
            c="teal"
          >
            {item.titleName}
          </Text>
        </UnstyledButton>
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

function ReviewList({
  jobId,
  onOpenTitle,
}: {
  jobId: string;
  onOpenTitle: (titleId: string) => void;
}) {
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
            onOpenTitle={onOpenTitle}
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
export function RunPanel({ jobId, onDismiss, onOpenTitle, onRunChange }: RunPanelProps) {
  const cache = useQueryClient();
  const run = useEnrichmentRun(jobId);
  // The global link shares the workspace's counts entry but never polls it; counts cost about 1.8 s at 10k titles.
  const counts = useQuery({
    ...enrichmentWorkspaceCountsQuery(''),
    staleTime: 60_000,
  });
  const { discardThen } = useDraftGuard();
  const retry = useRetryEnrichmentRun(jobId);
  const cancel = useCancelJob();
  const [reviewOpenFor, setReviewOpenFor] = useState<string | null>(null);
  const reviewOpen = reviewOpenFor === jobId;
  // An open draft is confirmed before anything is queued; after Discard the run switch navigates once.
  // Failures are shown from the mutation's own state.
  const startRetry = (status: RunRetryStatus) =>
    void discardThen(async () => onRunChange(await retry.mutateAsync(status))).catch(
      () => undefined,
    );
  const cancelRun = () =>
    cancel.mutate(jobId, {
      onSuccess: () => void invalidateEnrichmentWorkspace(cache),
    });
  const data = run.data;
  const active = !!data && isRunActive(data.phase);
  return (
    <section
      className={workspace.panel}
      aria-label="Enrichment run"
    >
      <Stack gap="xs">
        <Group
          justify="space-between"
          wrap="nowrap"
        >
          <Text fw={600}>{data ? runHeading(data) : 'This run'}</Text>
          <CloseButton
            aria-label="Dismiss run"
            onClick={onDismiss}
          />
        </Group>
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
            <Text size="sm">{executionLine(run.data)}</Text>
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
                    onClick={() => setReviewOpenFor(reviewOpen ? null : jobId)}
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
                {active && (
                  <Button
                    size="xs"
                    variant="subtle"
                    color="red"
                    loading={cancel.isPending}
                    onClick={cancelRun}
                  >
                    Cancel run
                  </Button>
                )}
              </Group>
              <Anchor
                component={Link}
                to="/enrichment?view=NeedsReview"
                size="sm"
              >
                {globalReviewLabel(counts.data?.needsReview)}
              </Anchor>
            </Group>
            {retry.isError && <Alert color="red">{retry.error.message}</Alert>}
            {cancel.isError && <Alert color="red">Could not cancel this run. Try again.</Alert>}
            {reviewOpen && run.data.review > 0 && (
              <ReviewList
                jobId={jobId}
                onOpenTitle={onOpenTitle}
              />
            )}
          </>
        )}
      </Stack>
    </section>
  );
}
