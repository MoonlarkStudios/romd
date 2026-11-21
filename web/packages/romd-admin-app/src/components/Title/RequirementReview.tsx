import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { EnrichmentAssessment, EnrichmentRequirement } from '@romd/admin-api-client';
import { type MouseEventHandler, useEffect, useState } from 'react';
import { Link } from 'react-router';
import { useSavedArtwork } from '../../hooks/api/useArtwork';
import { useProviderMatches } from '../../hooks/api/useProviderMatches';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { EnrichmentLauncher } from '../Enrichment/EnrichmentLauncher';
import { RequirementList } from '../Workspace/RequirementList';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { artworkLibraryEntries } from './artworkLibrary';
import { DecisionConfirmation } from './DecisionConfirmation';
import {
  type DecisionConfirmationCopy,
  enrichActionLabel,
  requirementCopy,
  requirementProviderId,
  requirementSubjectLabel,
  returnToAutomaticLabel,
  reviewActionLabel,
  revisitOmissionLabel,
} from './enrichmentPresentation';
import { canQuickConfirmMatch, QuickRequirementDecision } from './QuickRequirementDecision';
import { titleRequirementPath } from './titleSections';
import {
  currentRequirement,
  isReviewAction,
  processingOperation,
  type WorkbenchView,
} from './titleWorkbenchModel';
import { confirmedActions, useRequirementDecisions } from './useRequirementDecisions';

interface Confirmation {
  copy: DecisionConfirmationCopy;
  apply: () => Promise<unknown>;
}

export function RequirementReview({
  assessment,
  currentKey,
  view = 'All',
  heading,
  navigationState,
  reviewReturnTo,
  onOpenEditor,
  onRefresh,
  onDecisionOpen,
  onDecisionSaved,
}: {
  assessment: EnrichmentAssessment;
  currentKey?: string;
  view?: WorkbenchView;
  heading?: boolean;
  navigationState?: unknown;
  reviewReturnTo?: string;
  onOpenEditor?: MouseEventHandler<HTMLAnchorElement>;
  onRefresh: () => void;
  onDecisionOpen?: (opened: boolean) => void;
  onDecisionSaved?: () => void;
}) {
  const { canConfigureIntegrations } = usePermissions();
  const decisions = useRequirementDecisions(assessment);
  const [confirmation, setConfirmation] = useState<Confirmation>();
  const [quick, setQuick] = useState<EnrichmentRequirement>();
  const [quickPending, setQuickPending] = useState(false);
  const [launching, setLaunching] = useState(false);
  const current = currentRequirement(assessment.requirements, view, currentKey);
  const needsChoice = (action: string) =>
    assessment.requirements.some(
      (row) => row.state !== 'Satisfied' && row.actions.includes(action),
    );
  const needsArtwork = needsChoice('ReviewArtwork');
  const matches = useProviderMatches(
    assessment.titleId,
    needsChoice('ReviewProvider') ||
      assessment.requirements.some((row) => row.kind === 'Identity' && row.state === 'Processing'),
  );
  const savedArtwork = useSavedArtwork(assessment.titleId, needsArtwork);
  const title = useTitleDetail(needsArtwork ? assessment.titleId : undefined);
  const artworkEntries =
    title.data && savedArtwork.data
      ? artworkLibraryEntries(title.data.media, savedArtwork.data, title.data.artwork ?? [])
      : undefined;
  const providerJob = matches.data?.find(
    (match) =>
      match.pendingJobId &&
      assessment.requirements.some(
        (row) =>
          row.kind === 'Identity' &&
          row.state === 'Processing' &&
          requirementProviderId(row) === match.providerId,
      ),
  )?.pendingJobId;
  useEffect(
    () => () => onDecisionOpen?.(false),
    [
      onDecisionOpen,
    ],
  );
  const operation = processingOperation(assessment.requirements);
  const reversalRows = assessment.requirements.filter(
    (row) => row.satisfiedBy === 'AcceptedOmission' || row.satisfiedBy === 'LeftEmpty',
  );
  const closeDecision = () => {
    setConfirmation(undefined);
    setQuick(undefined);
    setQuickPending(false);
    onDecisionOpen?.(false);
  };

  const saved = () => {
    closeDecision();
    onDecisionSaved?.();
  };

  return (
    <Stack gap="sm">
      {(operation || providerJob) && (
        <Alert
          color="blue"
          role="status"
        >
          <Group justify="space-between">
            <Text size="sm">
              {operation ? requirementCopy(operation) : 'Provider work is running.'}
            </Text>
            {providerJob && (
              <Button
                component={Link}
                to={`/jobs/${encodeURIComponent(providerJob)}`}
                state={
                  reviewReturnTo
                    ? {
                        reviewReturnTo,
                      }
                    : undefined
                }
                size="compact-sm"
                variant="subtle"
              >
                View job
              </Button>
            )}
          </Group>
        </Alert>
      )}
      {decisions.error && (
        <Alert color="red">
          <Text size="sm">{decisions.error.message}</Text>
          <Button
            variant="subtle"
            onClick={() => {
              decisions.reset();
              onRefresh();
            }}
          >
            Refresh decision
          </Button>
        </Alert>
      )}
      <RequirementList
        heading={heading}
        assessment={assessment}
        currentKey={currentKey}
        state={navigationState}
        onOpen={onOpenEditor}
        href={(row) => titleRequirementPath(assessment.titleId, row)}
        renderDecision={(row) => {
          const review = row.actions.find(isReviewAction);
          const match =
            review === 'ReviewProvider'
              ? matches.data?.find((item) => item.providerId === requirementProviderId(row))
              : undefined;
          const editor =
            (review === 'ReviewProvider' && !matches.isLoading && !canQuickConfirmMatch(match)) ||
            (review === 'ReviewArtwork' &&
              !title.isLoading &&
              !savedArtwork.isLoading &&
              !artworkEntries?.some(
                (entry) => entry.url && (!entry.asset || entry.asset.role === row.subject),
              ));
          return (
            <Group gap={4}>
              {review &&
                row.state !== 'Satisfied' &&
                (editor ? (
                  <Button
                    {...workspaceActionProps}
                    component={Link}
                    to={titleRequirementPath(assessment.titleId, row)}
                    state={navigationState}
                    onClick={onOpenEditor}
                    variant="light"
                    size="compact-sm"
                    data-review-primary={row.key === current?.key || undefined}
                  >
                    {reviewActionLabel(review, row)}
                  </Button>
                ) : (
                  <Button
                    {...workspaceActionProps}
                    variant="light"
                    size="compact-sm"
                    data-review-primary={row.key === current?.key || undefined}
                    loading={
                      (review === 'ReviewProvider' && matches.isLoading) ||
                      (review === 'ReviewArtwork' && (title.isLoading || savedArtwork.isLoading))
                    }
                    onClick={() => {
                      setQuick(row);
                      onDecisionOpen?.(true);
                    }}
                  >
                    {reviewActionLabel(review, row)}
                  </Button>
                ))}
              {confirmedActions
                .filter((action) => row.actions.includes(action))
                .map((action, index) => {
                  const decision = decisions.confirmations[action];
                  return (
                    <Button
                      key={action}
                      {...workspaceActionProps}
                      variant="subtle"
                      size="compact-sm"
                      data-review-primary={
                        (!review && index === 0 && row.key === current?.key) || undefined
                      }
                      onClick={() => {
                        setConfirmation({
                          copy: decision.copy(row),
                          apply: decision.prepare(row),
                        });
                        onDecisionOpen?.(true);
                      }}
                    >
                      {decision.copy(row).confirmLabel}
                    </Button>
                  );
                })}
              {(row.actions.includes('Enrich') || row.actions.includes('Retry')) && (
                <Button
                  {...workspaceActionProps}
                  variant="subtle"
                  size="compact-sm"
                  disabled={assessment.hasActiveWork}
                  onClick={() => {
                    setLaunching(true);
                    onDecisionOpen?.(true);
                  }}
                >
                  {enrichActionLabel(row)}
                </Button>
              )}
              {canConfigureIntegrations && row.actions.includes('ConfigureProvider') && (
                <Button
                  component={Link}
                  to="/integrations"
                  variant="subtle"
                  size="compact-sm"
                >
                  Provider settings
                </Button>
              )}
            </Group>
          );
        }}
      />
      {reversalRows.length > 0 && (
        <details>
          <summary>Previous decisions ({reversalRows.length})</summary>
          <Stack
            gap="xs"
            mt="xs"
          >
            {reversalRows.map((row) => {
              const omission = row.satisfiedBy === 'AcceptedOmission';
              return (
                <Group
                  key={row.key}
                  justify="space-between"
                  gap="xs"
                >
                  <Text size="xs">{requirementSubjectLabel(row)}</Text>
                  <Button
                    size="compact-xs"
                    variant="subtle"
                    loading={decisions.pendingSubject === row.subject}
                    onClick={() =>
                      void (
                        omission ? decisions.revisit(row) : decisions.returnToAutomatic(row)
                      ).catch(() => undefined)
                    }
                  >
                    {omission
                      ? revisitOmissionLabel(row.subject)
                      : returnToAutomaticLabel(row.subject)}
                  </Button>
                </Group>
              );
            })}
          </Stack>
        </details>
      )}
      <DecisionConfirmation
        copy={confirmation?.copy}
        onCancel={closeDecision}
        onConfirm={() => confirmation?.apply() ?? Promise.resolve()}
        onConfirmed={saved}
        returnFocusAfterConfirm={!onDecisionSaved}
      />
      <Modal
        opened={!!quick}
        title={quick ? requirementSubjectLabel(quick) : undefined}
        onClose={closeDecision}
        closeOnEscape={!quickPending}
        closeOnClickOutside={!quickPending}
        withCloseButton={!quickPending}
      >
        {quick && (
          <QuickRequirementDecision
            key={quick.key}
            titleId={assessment.titleId}
            requirement={quick}
            onSaved={saved}
            onPending={setQuickPending}
            navigationState={navigationState}
            onOpenEditor={onOpenEditor}
          />
        )}
      </Modal>
      <EnrichmentLauncher
        reviewReturnTo={reviewReturnTo}
        opened={launching}
        onClose={() => {
          setLaunching(false);
          onDecisionOpen?.(false);
        }}
        titleId={assessment.titleId}
        titleName={assessment.titleName}
        initialMode="Enrich"
      />
    </Stack>
  );
}
