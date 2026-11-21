import { Alert, Button, Group, Radio, Skeleton, Stack, Text } from '@mantine/core';
import type { EnrichmentRequirement, TitleProviderMatch } from '@romd/admin-api-client';
import { replaceTitleArtworkRole } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch, TitleConflictError } from '../../hooks/api/titleConflict';
import { storeArtwork, useArtworkSelections, useSavedArtwork } from '../../hooks/api/useArtwork';
import { useProviderMatchDecision, useProviderMatches } from '../../hooks/api/useProviderMatches';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { useSetFieldOverrides, useTitleEnrichment } from '../../hooks/api/useTitleEnrichment';
import { ArtworkFrame } from '../ArtworkFrame';
import { isArtworkRole } from '../artworkPresentation';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { artworkLibraryEntries, artworkTargetForRole } from './artworkLibrary';
import { requirementProviderId } from './enrichmentPresentation';
import {
  displayValue,
  layerValue,
  METADATA_FIELDS,
  sourceLabel,
  sourceOptions,
} from './metadataDraft';
import { titleRequirementPath } from './titleSections';

interface DecisionProps {
  titleId: string;
  requirement: EnrichmentRequirement;
  onSaved: () => void;
  onPending?: (pending: boolean) => void;
  navigationState?: unknown;
  onOpenEditor?: React.MouseEventHandler<HTMLAnchorElement>;
}

function FullEditor({ titleId, requirement, navigationState, onOpenEditor }: DecisionProps) {
  return (
    <Button
      component={Link}
      to={titleRequirementPath(titleId, requirement)}
      state={navigationState}
      onClick={onOpenEditor}
      {...workspaceActionProps}
      variant="light"
    >
      Open title editor
    </Button>
  );
}

export function QuickRequirementDecision(props: DecisionProps) {
  if (props.requirement.kind === 'Identity') return <MatchDecision {...props} />;
  if (props.requirement.kind === 'Metadata') return <MetadataDecision {...props} />;
  if (props.requirement.kind === 'Artwork') return <ArtworkDecision {...props} />;
  return <FullEditor {...props} />;
}

function useReportPending(pending: boolean, report?: (pending: boolean) => void) {
  useEffect(() => {
    report?.(pending);
  }, [
    pending,
    report,
  ]);
}

export function canQuickConfirmMatch(match: TitleProviderMatch | undefined) {
  return match?.isAvailable && !!match.game && (match.decision?.candidates.length ?? 0) <= 1;
}

function MatchDecision(props: DecisionProps) {
  const query = useProviderMatches(props.titleId);
  const mutation = useProviderMatchDecision(props.titleId);
  useReportPending(mutation.isPending, props.onPending);
  const match = query.data?.find(
    (item) => item.providerId === requirementProviderId(props.requirement),
  );
  if (query.isPending)
    return (
      <Skeleton
        h={70}
        aria-label="Loading provider match"
      />
    );
  if (query.isError) return <Alert color="red">{query.error.message}</Alert>;
  if (!match?.game || !canQuickConfirmMatch(match))
    return (
      <Stack>
        <Text size="sm">Open the title to compare candidates or find a match.</Text>
        <FullEditor {...props} />
      </Stack>
    );
  return (
    <Stack>
      <Text size="sm">
        Confirm <strong>{match.game.name}</strong>
        {match.game.year ? ` (${match.game.year})` : ''} as the match from {match.providerName}.
      </Text>
      {mutation.error && <Alert color="red">{mutation.error.message}</Alert>}
      <Group justify="flex-end">
        <Button
          {...workspaceActionProps}
          loading={mutation.isPending}
          onClick={() =>
            mutation.mutate(
              {
                row: match,
                unlink: false,
              },
              {
                onSuccess: props.onSaved,
              },
            )
          }
        >
          Accept match
        </Button>
      </Group>
    </Stack>
  );
}

function MetadataDecision(props: DecisionProps) {
  const query = useTitleEnrichment(props.titleId);
  const mutation = useSetFieldOverrides();
  const cache = useQueryClient();
  const [refreshing, setRefreshing] = useState(false);
  useReportPending(mutation.isPending || refreshing, props.onPending);
  const [choice, setChoice] = useState<{
    id: string;
    revision: string;
  }>();
  const field = METADATA_FIELDS.find((item) => item.overrideKey === props.requirement.subject);
  const state = query.data;
  if (query.isPending)
    return (
      <Skeleton
        h={70}
        aria-label="Loading metadata sources"
      />
    );
  if (!state || !field?.overrideKey || query.isError) return <FullEditor {...props} />;
  const options = sourceOptions(state.layers, field);
  const overrideKey = field.overrideKey;
  if (!options.length)
    return (
      <Stack>
        <Text size="sm">
          No provider has supplied this value. Open Metadata to enter it manually.
        </Text>
        <FullEditor {...props} />
      </Stack>
    );
  return (
    <Stack>
      <Radio.Group
        value={choice?.id ?? ''}
        onChange={(id) =>
          setChoice({
            id,
            revision: state.revision,
          })
        }
        label={`Source for ${field.label.toLowerCase()}`}
      >
        <Stack
          mt="xs"
          gap="xs"
        >
          {options.map((layer) => (
            <Radio
              key={layer.sourceId}
              disabled={refreshing || mutation.isPending}
              value={layer.sourceId}
              label={sourceLabel(layer.sourceId)}
              description={displayValue(layerValue(layer, field))}
            />
          ))}
        </Stack>
      </Radio.Group>
      {mutation.error && <Alert color="red">{mutation.error.message}</Alert>}
      <Group justify="flex-end">
        <Button
          {...workspaceActionProps}
          disabled={!choice || !options.some((layer) => layer.sourceId === choice.id)}
          loading={mutation.isPending || refreshing}
          onClick={() =>
            choice &&
            mutation.mutate(
              {
                titleId: props.titleId,
                revision: choice.revision,
                overrides: {
                  [overrideKey]: choice.id,
                },
              },
              {
                onSuccess: props.onSaved,
                onError: (error) => {
                  if (!(error instanceof TitleConflictError)) return;
                  setChoice(undefined);
                  setRefreshing(true);
                  void invalidateTitle(cache, props.titleId).finally(() => setRefreshing(false));
                },
              },
            )
          }
        >
          Use source
        </Button>
      </Group>
    </Stack>
  );
}

function ArtworkDecision(props: DecisionProps) {
  const { titleId, requirement, onSaved } = props;
  const title = useTitleDetail(titleId);
  const saved = useSavedArtwork(titleId);
  const selections = useArtworkSelections(titleId);
  const cache = useQueryClient();
  const [choice, setChoice] = useState<{
    id: string;
    revision: string;
  }>();
  const [busy, setBusy] = useState(false);
  useReportPending(busy, props.onPending);
  const [error, setError] = useState('');
  const role = requirement.subject;
  const selection = selections.data?.roles.find((item) => item.role === role);
  if (title.isPending || saved.isPending || selections.isPending)
    return (
      <Skeleton
        h={90}
        aria-label="Loading saved artwork"
      />
    );
  if (!isArtworkRole(role) || !selection || !title.data || !saved.data)
    return <FullEditor {...props} />;
  const entries = artworkLibraryEntries(
    title.data.media,
    saved.data,
    title.data.artwork ?? [],
  ).filter((entry) => entry.url && (!entry.asset || entry.asset.role === role));
  const entry = entries.find((candidate) => candidate.key === choice?.id);
  const apply = async () => {
    if (!entry || !choice || busy) return;
    setBusy(true);
    setError('');
    try {
      const target = artworkTargetForRole(entry, role);
      const result = await replaceTitleArtworkRole({
        path: {
          titleId,
          role,
        },
        headers: ifMatch(choice.revision),
        body: {
          mode: 'Pinned',
          assetId: target.assetId,
          mediaId: target.mediaId,
        },
      });
      if (result.response?.status === 412 || result.response?.status === 409) {
        await invalidateTitle(cache, titleId);
        setChoice(undefined);
        throw new Error('The artwork changed. Review the reloaded choices before saving.');
      }
      if (result.error || !result.data) throw new Error('Could not use the selected artwork.');
      await storeArtwork(cache, titleId, result.data);
      await invalidateTitle(cache, titleId);
      onSaved();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not use the selected artwork.');
    } finally {
      setBusy(false);
    }
  };
  return (
    <Stack>
      {entries.length > 0 && (
        <Radio.Group
          value={choice?.id ?? ''}
          onChange={(id) =>
            setChoice({
              id,
              revision: selection.revision,
            })
          }
          label="Saved images"
        >
          <Stack
            gap="xs"
            mt="xs"
            mah="40dvh"
            style={{
              overflowY: 'auto',
            }}
          >
            {entries.map((item, index) => (
              <Radio
                key={item.key}
                disabled={busy}
                value={item.key}
                label={
                  <Group
                    gap="xs"
                    wrap="nowrap"
                  >
                    <div
                      style={{
                        width: 48,
                      }}
                    >
                      <ArtworkFrame
                        artworkRole={role}
                        src={item.url}
                        title={item.name}
                        loading="lazy"
                      />
                    </div>
                    <Text size="sm">
                      {item.name} {index + 1}
                      <Text
                        component="span"
                        c="dimmed"
                        size="xs"
                      >
                        {' '}
                        · {item.source}
                      </Text>
                    </Text>
                  </Group>
                }
              />
            ))}
          </Stack>
        </Radio.Group>
      )}
      {!entries.length && (
        <Text size="sm">Open Artwork to find provider images or upload a file.</Text>
      )}
      {error && <Alert color="red">{error}</Alert>}
      <Group justify="space-between">
        <FullEditor {...props} />
        {!!entries.length && (
          <Button
            {...workspaceActionProps}
            disabled={!entry}
            loading={busy}
            onClick={() => void apply()}
          >
            Use artwork
          </Button>
        )}
      </Group>
    </Stack>
  );
}
