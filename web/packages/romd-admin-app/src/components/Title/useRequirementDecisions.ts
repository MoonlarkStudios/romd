import {
  type EnrichmentAssessmentDto,
  type EnrichmentRequirementDto,
  replaceTitleArtworkRole,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch } from '../../hooks/api/titleConflict';
import { artworkSelectionsQuery, storeArtwork } from '../../hooks/api/useArtwork';
import { useEnrichmentOmission } from '../../hooks/api/useEnrichmentWorkspace';
import { useLeaveArtworkEmpty } from '../../hooks/api/useLeaveArtworkEmpty';
import { providerMatchesQuery, useProviderMatchDecision } from '../../hooks/api/useProviderMatches';
import { isArtworkRole } from '../artworkPresentation';
import {
  acceptOmissionConfirmation,
  continueWithoutProviderConfirmation,
  type DecisionConfirmationCopy,
  leaveEmptyConfirmation,
  requirementProviderId,
} from './enrichmentPresentation';

export const confirmedActions = [
  'AcceptOmission',
  'ContinueWithoutProvider',
  'LeaveEmpty',
] as const;
export type ConfirmedAction = (typeof confirmedActions)[number];

interface Confirmation {
  copy: (row: EnrichmentRequirementDto) => DecisionConfirmationCopy;
  /** Called when the confirmation opens, so the revision the write sends is already read when the curator confirms. */
  prepare?: () => void;
  apply: (row: EnrichmentRequirementDto) => Promise<unknown>;
}

/**
 * Immediate curation decisions on one title's requirements. Omission decisions are guarded by the title
 * revision of the assessment the curator decided on; reversals undo a satisfied decision. Every success
 * refreshes each surface of the title.
 */
export function useRequirementDecisions(assessment: EnrichmentAssessmentDto) {
  const { titleId } = assessment;
  const cache = useQueryClient();
  const omission = useEnrichmentOmission();
  const providerDecision = useProviderMatchDecision(titleId);
  const leaveEmpty = useLeaveArtworkEmpty(titleId);
  // The revision the curator saw: a fresh read here would accept changes they never reviewed.
  const setOmission = (field: string, accepted: boolean) =>
    omission.mutateAsync({
      titleId,
      field,
      accepted,
      revision: assessment.revision,
    });
  const revisit = useMutation({
    mutationFn: (field: string) => setOmission(field, false),
  });
  // The role as the title's surfaces show it; only an empty cache fetches, since a precondition compared with a
  // just-fetched revision would hide changes the curator never saw.
  const shownRole = async (role: string) =>
    (await cache.ensureQueryData(artworkSelectionsQuery(titleId))).roles.find(
      (item) => item.role === role,
    );
  // Every artwork decision sends the role revision the title shows, so the decisions observe the roles from
  // mount: they are in the cache before any click, and a title refresh keeps them current.
  useQuery(artworkSelectionsQuery(titleId));
  // A confirmation reads what an empty cache lacks when it opens, never again when the curator confirms.
  const prepareMatches = () => void cache.ensureQueryData(providerMatchesQuery(titleId));
  const automatic = useMutation({
    mutationFn: async (role: string) => {
      const selection = await shownRole(role);
      if (!selection || !isArtworkRole(role))
        throw new Error(
          'This artwork role is not available. Refresh the decision, then try again.',
        );
      const response = await replaceTitleArtworkRole({
        path: {
          titleId,
          role,
        },
        headers: ifMatch(selection.revision),
        body: {
          mode: 'Automatic',
        },
      });
      if (response.response?.status === 412) {
        await invalidateTitle(cache, titleId);
        throw new Error('The artwork changed since you saw it. Review it, then decide again.');
      }
      if (response.error || !response.data)
        throw new Error(response.error?.detail ?? 'Could not return to automatic. Try again.');
      await storeArtwork(cache, titleId, response.data);
    },
    onSuccess: () => invalidateTitle(cache, titleId),
  });
  const confirmations: Record<ConfirmedAction, Confirmation> = {
    AcceptOmission: {
      copy: (row) => acceptOmissionConfirmation(row.subject),
      apply: (row) => setOmission(row.subject, true),
    },
    ContinueWithoutProvider: {
      copy: (row) => continueWithoutProviderConfirmation(requirementProviderId(row) ?? row.subject),
      prepare: prepareMatches,
      apply: async (row) => {
        // The match as the title's surfaces show it; only an empty cache fetches, since a precondition compared
        // with a just-fetched revision would hide changes the curator never saw.
        const match = (await cache.ensureQueryData(providerMatchesQuery(titleId))).find(
          (item) => item.providerId === requirementProviderId(row),
        );
        if (!match?.isAvailable)
          throw new Error(
            'This provider is not available. Check provider settings, then try again.',
          );
        await providerDecision.mutateAsync({
          row: match,
          unlink: true,
        });
      },
    },
    LeaveEmpty: {
      copy: (row) => leaveEmptyConfirmation(row.subject),
      apply: async ({ subject }) => {
        const selection = await shownRole(subject);
        if (!selection || !isArtworkRole(subject))
          throw new Error(
            'This artwork role is not available. Refresh the decision, then try again.',
          );
        await leaveEmpty.mutateAsync({
          role: subject,
          revision: selection.revision,
        });
      },
    },
  };
  const reversal = revisit.isPending
    ? revisit.variables
    : automatic.isPending
      ? automatic.variables
      : undefined;
  return {
    confirmations,
    revisit: (row: EnrichmentRequirementDto) => revisit.mutateAsync(row.subject),
    returnToAutomatic: (row: EnrichmentRequirementDto) => automatic.mutateAsync(row.subject),
    /** The subject of the reversal in flight, so only its row shows progress. */
    pendingSubject: reversal,
    error: revisit.error ?? automatic.error,
    reset: () => {
      revisit.reset();
      automatic.reset();
    },
  };
}
