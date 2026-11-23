import { Alert, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import type {
  EnrichmentAssessment,
  EnrichmentRequirement,
  TitleProviderMatch,
} from '@romd/admin-api-client';
import { IconArrowRight, IconCheck } from '@tabler/icons-react';
import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { useDiscardEpoch } from '../../components/Administration/DraftGuard';
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
  providerBusy: boolean;
  onProviderBusyChange: (busy: boolean) => void;
}

function isProviderDecision(issue: EnrichmentRequirement) {
  return (
    issue.kind === 'Identity' &&
    issue.state === 'NeedsReview' &&
    issue.actions.includes('ReviewProvider')
  );
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
  providerBusy,
  onProviderBusyChange,
}: CatalogReviewFocusProps) {
  const query = useEnrichmentAssessment(titleId);
  const assessment = query.data ?? fallback;
  const [confirmed, setConfirmed] = useState<{
    key: string;
    withoutProvider: boolean;
    jobId?: string | null;
  }>();
  const discardEpoch = useDiscardEpoch();
  const [pickerVersion, setPickerVersion] = useState(0);
  const [providerDraft, setProviderDraft] = useState<{
    issue: EnrichmentRequirement;
    row: TitleProviderMatch;
    discardEpoch: number;
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
  const draft = providerDraft?.discardEpoch === discardEpoch ? providerDraft : undefined;
  const current =
    draft?.issue ?? outstanding.find((row) => row.key === requirementKey) ?? decisions[0];
  const otherDecisions = decisions.filter((row) => row.key !== current?.key);
  const onDraftChange = useCallback(
    (row: TitleProviderMatch | undefined) => {
      setProviderDraft(
        current && row
          ? {
              issue: current,
              row,
              discardEpoch,
            }
          : undefined,
      );
    },
    [
      current,
      discardEpoch,
    ],
  );
  const navigationState = {
    catalogReturnTo: returnPath,
  };
  const openEditor: React.MouseEventHandler<HTMLAnchorElement> = (event) => {
    if (providerBusy) {
      event.preventDefault();
      return;
    }
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
      closeDisabled={providerBusy}
      navigation={{
        onPrevious: providerBusy ? undefined : previous,
        onNext: loadingNextTitle || providerBusy ? undefined : next,
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
              aria-disabled={providerBusy || undefined}
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
                aria-disabled={providerBusy || undefined}
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
                disabled={providerBusy}
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
                    disabled={providerBusy}
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
            disabled={providerBusy}
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
                  {confirmed.withoutProvider ? 'Provider matching turned off' : 'Match confirmed'}
                </Title>
              </Group>
              <Text
                size="sm"
                c="dimmed"
              >
                {confirmed.withoutProvider
                  ? 'You can link this provider later from the title.'
                  : 'Follow-up enrichment was queued. You can keep reviewing.'}
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
          ) : current && isProviderDecision(current) ? (
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
                key={`${current.key}:${discardEpoch}:${pickerVersion}`}
                assessment={assessment}
                issue={current}
                draftRow={draft?.row}
                staleIssue={
                  !!draft &&
                  !decisions.some((row) => row.key === current.key && isProviderDecision(row))
                }
                onDraftChange={onDraftChange}
                onBusyChange={onProviderBusyChange}
                showLatest={() => {
                  setProviderDraft(undefined);
                  setPickerVersion((version) => version + 1);
                  document.getElementById(catalogInspectorId)?.focus();
                }}
                skip={skip}
                loadingNextTitle={loadingNextTitle}
                confirm={(match) => {
                  setProviderDraft(undefined);
                  setConfirmed({
                    key: current.key,
                    withoutProvider: match.state === 'Unlinked',
                    jobId: match.pendingJobId,
                  });
                }}
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
  draftRow,
  staleIssue,
  onDraftChange,
  onBusyChange,
  showLatest,
  skip,
  loadingNextTitle,
  confirm,
}: {
  assessment: EnrichmentAssessment;
  issue: EnrichmentRequirement;
  draftRow?: TitleProviderMatch;
  staleIssue: boolean;
  onDraftChange: (row: TitleProviderMatch | undefined) => void;
  onBusyChange: (busy: boolean) => void;
  showLatest: () => void;
  skip: () => void;
  loadingNextTitle: boolean;
  confirm: (match: TitleProviderMatch) => void;
}) {
  const matches = useProviderMatches(assessment.titleId);
  const row = matches.data?.find((match) => match.providerId === requirementProviderId(issue));
  const shown = draftRow ?? row;
  if (!shown && matches.isPending)
    return (
      <Skeleton
        h={160}
        aria-label="Loading provider candidates"
      />
    );
  if (!shown?.isAvailable)
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
      row={shown}
      onClose={(saved, match) => {
        if (saved && match) confirm(match);
      }}
      review={{
        platformName: assessment.platformName,
        thumbnailUrl: assessment.thumbnailUrl,
        onSkip: skip,
        loadingNextTitle,
        canContinueWithout: issue.actions.includes('ContinueWithoutProvider'),
        stale:
          staleIssue || (!!draftRow && (!row?.isAvailable || row.revision !== draftRow.revision)),
        onShowLatest: showLatest,
        onDraftChange,
        onBusyChange,
      }}
    />
  );
}
