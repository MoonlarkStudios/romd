import { Alert, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import type { EnrichmentAssessment, EnrichmentRequirement } from '@romd/admin-api-client';
import { IconArrowRight, IconCheck } from '@tabler/icons-react';
import { useLayoutEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import {
  outstandingRequirements,
  requirementCopy,
  requirementProviderId,
  requirementSubjectLabel,
} from '../../components/Title/enrichmentPresentation';
import { ProviderMatchPicker } from '../../components/Title/ProviderMatches';
import { RequirementReview } from '../../components/Title/RequirementReview';
import { titleRequirementPath, titleSectionPath } from '../../components/Title/titleSections';
import { InspectorPanel } from '../../components/Workspace/InspectorPanel';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import { useProviderMatches } from '../../hooks/api/useProviderMatches';
import { catalogInspectorId } from './CatalogInspector';
import { rememberCatalogPosition } from './catalogNavigation';

interface CatalogReviewFocusProps {
  titleId: string;
  fallback?: EnrichmentAssessment;
  requirementKey?: string;
  returnPath: string;
  libraryPath?: string;
  onViewBlocked: () => void;
  close: () => void;
  previous?: () => void;
  next?: () => void;
  skip: () => void;
  loadingNextTitle: boolean;
  nextTitleError: boolean;
  selectRequirement: (key: string) => void;
  onDecisionOpen: (open: boolean) => void;
  onDecisionSaved: () => void;
}

export function CatalogReviewFocus({
  titleId,
  fallback,
  requirementKey,
  returnPath,
  libraryPath,
  onViewBlocked,
  close,
  previous,
  next,
  skip,
  loadingNextTitle,
  nextTitleError,
  selectRequirement,
  onDecisionOpen,
  onDecisionSaved,
}: CatalogReviewFocusProps) {
  const query = useEnrichmentAssessment(titleId);
  const assessment = query.data ?? fallback;
  const [confirmed, setConfirmed] = useState<{
    key: string;
    jobId?: string | null;
  }>();
  const confirmationHeading = useRef<HTMLHeadingElement>(null);
  useLayoutEffect(() => {
    if (confirmed) confirmationHeading.current?.focus();
  }, [
    confirmed,
  ]);
  const outstanding = outstandingRequirements(assessment?.requirements ?? []);
  const decisions = outstanding.filter(
    (row) => row.state === 'NeedsReview' && row.key !== confirmed?.key,
  );
  const current = outstanding.find((row) => row.key === requirementKey) ?? decisions[0];
  const otherDecisions = decisions.filter((row) => row.key !== current?.key);
  const reviewingMatch = current?.kind === 'Identity' && current.actions.includes('ReviewProvider');
  const navigationState = {
    catalogReturnTo: returnPath,
  };
  const openEditor: React.MouseEventHandler<HTMLAnchorElement> = (event) => {
    rememberCatalogPosition(returnPath, event);
  };
  const chooseRequirement = (key: string) => {
    selectRequirement(key);
    setConfirmed(undefined);
  };

  return (
    <InspectorPanel
      id={catalogInspectorId}
      title={assessment?.titleName ?? 'Title review'}
      onClose={close}
      navigation={{
        onPrevious: previous,
        onNext: loadingNextTitle ? undefined : next,
      }}
      footer={
        assessment && (
          <Group
            gap="xs"
            justify="space-between"
          >
            <Button
              component={Link}
              to={
                current && !confirmed
                  ? titleRequirementPath(titleId, current)
                  : titleSectionPath(titleId)
              }
              state={navigationState}
              onClick={openEditor}
              {...workspaceActionProps}
              variant="subtle"
              size="compact-sm"
            >
              {current && !confirmed ? 'Open in title' : 'Open title'}
            </Button>
            {libraryPath && (
              <Button
                component={Link}
                to={libraryPath}
                onClick={openEditor}
                variant="subtle"
                size="compact-sm"
              >
                Inspect in a library
              </Button>
            )}
            {!assessment.needsReview && assessment.hasOperationalBlocker && (
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={onViewBlocked}
              >
                View blocked queue
              </Button>
            )}
            {!confirmed && otherDecisions.length > 0 && (
              <Group
                gap="xs"
                aria-label="Other decisions"
              >
                <Text
                  size="xs"
                  c="dimmed"
                >
                  Also needs review
                </Text>
                {otherDecisions.map((issue) => (
                  <Button
                    key={issue.key}
                    variant="light"
                    size="compact-sm"
                    onClick={() => chooseRequirement(issue.key)}
                  >
                    {requirementSubjectLabel(issue)}
                  </Button>
                ))}
              </Group>
            )}
            {confirmed?.jobId && (
              <Button
                component={Link}
                to={`/jobs/${encodeURIComponent(confirmed.jobId)}`}
                state={{
                  reviewReturnTo: returnPath,
                }}
                {...workspaceActionProps}
                variant="subtle"
                size="compact-sm"
              >
                View job
              </Button>
            )}
          </Group>
        )
      }
    >
      {query.isError && (
        <Alert
          color="red"
          title="Could not refresh this title’s review"
        >
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={() => void query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {nextTitleError && (
        <Alert
          color="yellow"
          title="Could not load the next title"
        >
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={next}
          >
            Try again
          </Button>
        </Alert>
      )}
      {assessment ? (
        <Stack gap="md">
          {confirmed ? (
            <Stack
              gap="xs"
              role="status"
            >
              <Group gap="xs">
                <IconCheck
                  size={20}
                  aria-hidden="true"
                />
                <Title
                  order={3}
                  className="romd-card-title"
                  ref={confirmationHeading}
                  tabIndex={-1}
                >
                  Match confirmed
                </Title>
              </Group>
              <Text
                size="sm"
                c="dimmed"
              >
                Follow-up enrichment was queued. You can keep reviewing.
              </Text>
              <Group
                gap="xs"
                mt="xs"
              >
                {decisions[0] && (
                  <Button
                    {...workspaceActionProps}
                    onClick={() => chooseRequirement(decisions[0].key)}
                  >
                    Next decision
                  </Button>
                )}
                <Button
                  {...workspaceActionProps}
                  variant={decisions.length ? 'light' : 'filled'}
                  rightSection={next && <IconArrowRight size={16} />}
                  loading={loadingNextTitle}
                  onClick={next ?? close}
                >
                  {next ? 'Next title' : 'Back to queue'}
                </Button>
              </Group>
            </Stack>
          ) : reviewingMatch && current ? (
            <Stack gap="sm">
              <div>
                <Title
                  order={3}
                  className="romd-card-title"
                >
                  Match on {requirementSubjectLabel(current)}
                </Title>
                {assessment.hasActiveWork && (
                  <Text
                    role="status"
                    size="xs"
                    c="dimmed"
                  >
                    Enrichment is in progress for this title.
                  </Text>
                )}
                {current.cause !== 'MatchDecisionRequired' && (
                  <Text
                    size="sm"
                    c="dimmed"
                  >
                    {requirementCopy(current)}
                  </Text>
                )}
              </div>
              <CatalogProviderDecision
                key={current.key}
                assessment={assessment}
                issue={current}
                skip={skip}
                loadingNextTitle={loadingNextTitle}
                confirm={(jobId) =>
                  setConfirmed({
                    key: current.key,
                    jobId,
                  })
                }
              />
            </Stack>
          ) : (
            <>
              <RequirementReview
                assessment={assessment}
                view="NeedsReview"
                currentKey={current?.key}
                onlyCurrent
                navigationState={navigationState}
                reviewReturnTo={returnPath}
                onOpenEditor={openEditor}
                onRefresh={() => void query.refetch()}
                onDecisionOpen={onDecisionOpen}
                onDecisionSaved={onDecisionSaved}
              />
              {current && (
                <Button
                  variant="subtle"
                  color="gray"
                  w="fit-content"
                  loading={loadingNextTitle}
                  onClick={skip}
                >
                  Skip for now
                </Button>
              )}
              {!current && next && (
                <Button
                  {...workspaceActionProps}
                  w="fit-content"
                  loading={loadingNextTitle}
                  onClick={next}
                >
                  Next title
                </Button>
              )}
            </>
          )}
        </Stack>
      ) : (
        !query.isError && (
          <Skeleton
            h={160}
            aria-label="Loading title review"
          />
        )
      )}
    </InspectorPanel>
  );
}

function CatalogProviderDecision({
  assessment,
  issue,
  skip,
  loadingNextTitle,
  confirm,
}: {
  assessment: EnrichmentAssessment;
  issue: EnrichmentRequirement;
  skip: () => void;
  loadingNextTitle: boolean;
  confirm: (jobId: string | null | undefined) => void;
}) {
  const matches = useProviderMatches(assessment.titleId);
  const row = matches.data?.find((match) => match.providerId === requirementProviderId(issue));
  if (matches.isPending)
    return (
      <Skeleton
        h={160}
        aria-label="Loading provider candidates"
      />
    );
  if (matches.isError || !row?.isAvailable)
    return (
      <Stack
        align="start"
        gap="xs"
      >
        <Alert color="yellow">
          {matches.isError
            ? 'Could not load this provider’s candidates.'
            : 'This provider is currently unavailable.'}
        </Alert>
        <Group gap="xs">
          <Button
            variant="light"
            onClick={() => void matches.refetch()}
          >
            Retry
          </Button>
          <Button
            variant="subtle"
            color="gray"
            loading={loadingNextTitle}
            onClick={skip}
          >
            Skip for now
          </Button>
        </Group>
      </Stack>
    );
  return (
    <ProviderMatchPicker
      titleId={assessment.titleId}
      titleName={assessment.titleName}
      row={row}
      onClose={(saved, match) => {
        if (saved) confirm(match?.pendingJobId);
      }}
      review={{
        platformName: assessment.platformName,
        thumbnailUrl: assessment.thumbnailUrl,
        onSkip: skip,
        loadingNextTitle,
      }}
    />
  );
}
