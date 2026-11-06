import { Alert, Button, Group, Modal, Select, Stack, Text } from '@mantine/core';
import {
  type ArtworkCompletionFilterDto,
  previewArtworkCompletion,
  queueArtworkCompletion,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { invalidateEnrichmentWorkspace } from '../../hooks/api/enrichmentWorkspaceCache';
import { useCreateJob } from '../../hooks/api/useCreateJob';
import { createRequestId } from '../../utils/requestId';
import { useDraftGuard } from '../Administration/DraftGuard';
import { artworkRoleLabel } from '../artworkPresentation';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import {
  type EnrichmentLaunchMode,
  type EnrichmentLaunchScope,
  effectiveScope,
  launchRequest,
  launchScopes,
} from './enrichmentLaunch';

export type { EnrichmentLaunchMode, EnrichmentLaunchScope } from './enrichmentLaunch';

interface LaunchCommon {
  opened: boolean;
  onClose: () => void;
  /** Defaults to Artwork. */
  initialMode?: EnrichmentLaunchMode;
}

/** One title: the run covers exactly that title, so no scope is offered. */
interface TitleLaunch extends LaunchCommon {
  titleId: string;
  titleName?: string;
  titleIds?: never;
  filters?: never;
  initialScope?: never;
}

/** The entry point's selection, its current filters, automatic eligibility or every tracked title. */
interface ScopedLaunch extends LaunchCommon {
  titleId?: never;
  titleName?: never;
  /** Offered as the Selected scope when not empty. */
  titleIds?: string[];
  /** Offered as the Filtered scope and narrows Automatic. */
  filters?: ArtworkCompletionFilterDto;
  /** Defaults to AllTracked; a scope the entry point cannot offer falls back to Filtered, then AllTracked. */
  initialScope?: EnrichmentLaunchScope;
}

export type EnrichmentLauncherProps = TitleLaunch | ScopedLaunch;

const MODES = [
  {
    value: 'Enrich',
    label: 'Metadata and missing artwork',
  },
  {
    value: 'Artwork',
    label: 'Missing artwork only',
  },
];

/** The one way to start enrichment. The modal unmounts its content when closed, so each opening is a new launch with its own choices and request id. */
export function EnrichmentLauncher(props: EnrichmentLauncherProps) {
  return (
    <Modal.Root
      opened={props.opened}
      onClose={props.onClose}
      size="md"
    >
      <Modal.Overlay />
      <Modal.Content>
        <LaunchForm {...props} />
      </Modal.Content>
    </Modal.Root>
  );
}

function launchTitle(
  mode: EnrichmentLaunchMode,
  single: boolean,
  titleName = 'this title',
): string {
  if (!single) return mode === 'Enrich' ? 'Enrich titles' : 'Fill missing artwork';
  return mode === 'Enrich' ? `Enrich ${titleName}` : `Fill missing artwork for ${titleName}`;
}

function LaunchForm({
  opened,
  onClose,
  titleId,
  titleName,
  titleIds = [],
  filters,
  initialMode = 'Artwork',
  initialScope = 'AllTracked',
}: EnrichmentLauncherProps) {
  const navigate = useNavigate();
  const { discardThen } = useDraftGuard();
  const cache = useQueryClient();
  const [mode, setMode] = useState(initialMode);
  const [requestedScope, setScope] = useState(initialScope);
  const single = titleId !== undefined;
  const options = single
    ? []
    : launchScopes({
        mode,
        selectedCount: titleIds.length,
        hasFilters: !!filters,
      });
  const scope = single ? 'Selected' : effectiveScope(requestedScope, options);
  const requestId = useRef(createRequestId());
  const body = launchRequest({
    requestId: requestId.current,
    scope,
    mode,
    titleIds: single
      ? [
          titleId,
        ]
      : titleIds,
    filters,
  });
  const signature = JSON.stringify({
    ...body,
    requestId: '',
  });
  // A different scope or mode is a different request; retrying a failed start reuses its id.
  const previousSignature = useRef(signature);
  useEffect(() => {
    if (previousSignature.current === signature) return;
    previousSignature.current = signature;
    requestId.current = createRequestId();
  }, [
    signature,
  ]);
  const preview = useQuery({
    queryKey: [
      'enrichment-launch-preview',
      signature,
    ],
    enabled: opened,
    queryFn: async ({ signal }) => {
      const result = await previewArtworkCompletion({
        body,
        signal,
      });
      if (result.error || !result.data)
        throw new Error('Could not check this scope. Retry before starting.');
      return result.data;
    },
  });
  const { seedJob } = useCreateJob();
  const start = useMutation({
    mutationFn: async () => {
      const result = await queueArtworkCompletion({
        body: {
          ...body,
          requestId: requestId.current,
        },
      });
      if (result.error || !result.data)
        throw new Error('Could not start enrichment. Retry safely with the same scope.');
      seedJob(result.data);
      return result.data.jobId;
    },
    onSuccess: async (jobId) => {
      await invalidateEnrichmentWorkspace(cache);
      navigate(`/enrichment?view=Processing&activity=${encodeURIComponent(jobId)}`);
      onClose();
    },
  });
  // An open draft is confirmed before anything is queued; after Discard the run page opens once.
  // Failures are shown from the mutation's own state.
  const launch = () => void discardThen(() => start.mutateAsync()).catch(() => undefined);
  const titles = preview.data?.titles ?? 0;
  return (
    <>
      <Modal.Header>
        <Modal.Title>{launchTitle(mode, single, titleName)}</Modal.Title>
        <Modal.CloseButton />
      </Modal.Header>
      <Modal.Body>
        <Stack>
          <Select
            label="What to enrich"
            value={mode}
            onChange={(value) => setMode(value === 'Enrich' ? 'Enrich' : 'Artwork')}
            data={MODES}
            allowDeselect={false}
          />
          {!single && (
            <Select
              label="Titles to check"
              value={scope}
              onChange={(value) =>
                setScope(options.find((option) => option.value === value)?.value ?? scope)
              }
              data={options}
              allowDeselect={false}
            />
          )}
          {preview.isFetching && (
            <Text>{single ? 'Checking this title…' : 'Checking titles…'}</Text>
          )}
          {preview.data && !single && (
            <Text>
              {titles.toLocaleString()} {titles === 1 ? 'title' : 'titles'} in this scope
            </Text>
          )}
          <Group gap="sm">
            {preview.data?.roles.map((row) => (
              <Text
                key={row.role}
                size="sm"
                c="dimmed"
              >
                {artworkRoleLabel(row.role)}: {row.count} missing
              </Text>
            ))}
          </Group>
          <Text size="sm">
            Existing artwork and your curation choices are preserved. Any provider decisions will
            appear in your enrichment workspace.
          </Text>
          {(preview.error || start.error) && (
            <Alert color="red">
              {(preview.error ?? start.error)?.message}
              {preview.error && (
                <Button
                  variant="subtle"
                  onClick={() => void preview.refetch()}
                >
                  Reload
                </Button>
              )}
            </Alert>
          )}
          <Group justify="end">
            <Button
              variant="default"
              onClick={onClose}
            >
              Cancel
            </Button>
            <Button
              {...workspaceActionProps}
              disabled={!titles || preview.isFetching || preview.isError}
              loading={start.isPending}
              onClick={launch}
            >
              {mode === 'Enrich' ? 'Start enrichment' : 'Start filling missing artwork'}
            </Button>
          </Group>
        </Stack>
      </Modal.Body>
    </>
  );
}
