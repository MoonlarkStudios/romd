import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  FileInput,
  Group,
  Image,
  Loader,
  Menu,
  Modal,
  NativeSelect,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Tabs,
  Text,
  Title,
  UnstyledButton,
} from '@mantine/core';
import type { ArtworkCandidate, ArtworkProvider, TitleProviderMatch } from '@romd/admin-api-client';
import {
  applyArtworkCandidate,
  deleteTitleArtworkAsset,
  importGalleryArtwork,
  listTitleArtworkCandidates,
  previewArtworkCandidate,
  replaceHiddenArtworkCandidate,
  replaceTitleArtworkRole,
} from '@romd/admin-api-client';
import { IconDotsVertical, IconPhoto, IconPlus, IconUpload } from '@tabler/icons-react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { useLocation, useSearchParams } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { invalidateTitle } from '../../hooks/api/titleCache';
import {
  ifMatch,
  revisionFromETag,
  shownTitleRevision,
  storeTitleRevision,
  TitleConflictError,
} from '../../hooks/api/titleConflict';
import {
  artworkKeys,
  storeArtwork,
  useArtworkProviders,
  useArtworkSelections,
  useSavedArtwork,
} from '../../hooks/api/useArtwork';
import { useJob } from '../../hooks/api/useJobs';
import { useLeaveArtworkEmpty } from '../../hooks/api/useLeaveArtworkEmpty';
import { useProviderMatches } from '../../hooks/api/useProviderMatches';
import { useDeleteTitleMedia, useUploadTitleMedia } from '../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { createRequestId } from '../../utils/requestId';
import { useDiscardEpoch, useDraft, useDraftGuard } from '../Administration/DraftGuard';
import { ArtworkSurface } from '../ArtworkSurface';
import {
  artworkPresentation,
  artworkRoleLabel,
  artworkRoles,
  artworkSourceLabel,
  backdropSuitable,
  isArtworkRole,
  type ArtworkRole as Role,
} from '../artworkPresentation';
import { isDocumentType } from '../mediaTypes';
import { InspectorLayout } from '../Workspace/InspectorLayout';
import { InspectorPanel } from '../Workspace/InspectorPanel';
import { useInspectorSelection, useInspectorSize } from '../Workspace/useInspectorSelection';
import workspaceClasses from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { AddArtwork, type AddMediaMode } from './AddArtwork';
import { ArtworkAcquisition } from './ArtworkAcquisition';
import { ArtworkComposer, type ArtworkDraft } from './ArtworkComposer';
import { ArtworkImageInspector } from './ArtworkImageInspector';
import classes from './ArtworkWorkspace.module.css';
import { artworkJobStatus } from './artworkJobStatus';
import { artworkLibraryEntries, type SavedArtworkTarget } from './artworkLibrary';
import { typeLabel } from './BatchArtworkUpload';
import { CandidateImage } from './CandidateImage';
import { DecisionConfirmation } from './DecisionConfirmation';
import { leaveEmptyConfirmation } from './enrichmentPresentation';
import { ManualCover } from './ManualViewer';
import { MediaGallery } from './MediaGallery';
import {
  type LibraryReleaseFilter,
  type LibraryTypeFilter,
  libraryTypeOptions,
  matchesLibraryFilter,
  placementLabel,
  releaseOptions,
} from './mediaLibrary';
import {
  PresentationRoleCard,
  rolePresentationState,
  roleStateLabel,
} from './PresentationRoleCard';
import { ProviderMatchPicker } from './ProviderMatches';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

function CandidateBrowser({
  titleId,
  title,
  role,
  provider,
  gameId,
  selectedReference,
  disabled,
  allowHide,
  onChoose,
}: {
  titleId: string;
  title: TitleDetail;
  role: Role;
  provider: ArtworkProvider;
  gameId: string;
  selectedReference?: string;
  disabled: boolean;
  allowHide: boolean;
  onChoose: (candidate: ArtworkCandidate) => Promise<void>;
}) {
  const cache = useQueryClient();
  const family = useArtworkSelections(titleId).data;
  const [dimension, setDimension] = useState<string | null>(null);
  const [style, setStyle] = useState<string | null>(null);

  const [visible, setVisible] = useState(12);
  const [suitableOnly, setSuitableOnly] = useState(
    role === 'Backdrop' && provider.id !== 'steamgriddb',
  );
  const [pending, setPending] = useState(false);
  const [hiding, setHiding] = useState(false);
  const unavailable = pending || disabled;
  const [error, setError] = useState<string | null>(null);
  const capability = provider.roles.find((item) => item.role === role);
  const candidates = useInfiniteQuery({
    queryKey: [
      ...artworkKeys.candidates(titleId),
      provider.id,
      gameId,
      role,
      dimension,
      style,
    ],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam, signal }) => {
      const response = await listTitleArtworkCandidates({
        path: {
          titleId,
        },
        query: {
          providerId: provider.id,
          gameId,
          role,
          dimension: dimension ?? undefined,
          style: style ?? undefined,
          cursor: pageParam ?? undefined,
        },
        signal,
      });
      if (response.error || !response.data)
        throw new Error(response.error?.detail ?? 'Could not browse artwork.');
      if (
        !Object.hasOwn(response.data, 'nextCursor') ||
        response.data.hasNextPage !== (typeof response.data.nextCursor === 'string') ||
        (response.data.hasNextPage && !response.data.nextCursor?.trim()) ||
        (pageParam !== null && response.data.nextCursor === pageParam)
      )
        throw new Error(
          'The provider returned an invalid continuation. Refresh candidates and try again.',
        );
      return response.data;
    },
    getNextPageParam: (page) => (page.hasNextPage ? page.nextCursor : undefined),
    staleTime: 0,
    refetchOnWindowFocus: false,
  });
  const items = Array.from(
    new Map(
      (candidates.data?.pages.flatMap((page) => page.items) ?? []).map((item) => [
        item.candidateId,
        item,
      ]),
    ).values(),
  );
  const displayed =
    role === 'Backdrop' && suitableOnly
      ? items
          .filter((item) => backdropSuitable(item.width, item.height))
          .sort(
            (a, b) =>
              Math.abs(Math.log(a.width / a.height / (16 / 9))) -
                Math.abs(Math.log(b.width / b.height / (16 / 9))) ||
              b.width * b.height - a.width * a.height,
          )
      : items;
  const changeFilters = (nextDimension: string | null, nextStyle: string | null) => {
    setDimension(nextDimension);
    setStyle(nextStyle);
    setVisible(12);
    setError(null);
  };
  const apply = async (candidate: ArtworkCandidate) => {
    setPending(true);
    setError(null);
    try {
      await onChoose(candidate);
    } catch {
      setError('Preview expired or unavailable. Refresh candidates and try again.');
    } finally {
      setPending(false);
    }
  };
  return (
    <Stack>
      {!!(capability?.dimensions.length || capability?.styles.length) && (
        <Group grow>
          {!!capability?.dimensions.length && (
            <Select
              label="Dimensions"
              placeholder="All supported dimensions"
              clearable
              value={dimension}
              data={capability.dimensions}
              onChange={(value) => changeFilters(value, style)}
              disabled={unavailable}
            />
          )}
          {!!capability?.styles.length && (
            <Select
              label="Style"
              placeholder="All styles"
              clearable
              value={style}
              data={capability.styles}
              onChange={(value) => changeFilters(dimension, value)}
              disabled={unavailable}
            />
          )}
        </Group>
      )}
      {role === 'Backdrop' && (
        <Stack gap="xs">
          <Text size="sm">
            Choose a scene without a built-in game logo. Thumbnails show a centered 16:9 crop.
            Select an image to adjust its focal point and check the composition preview.
          </Text>
          <Switch
            label="Suitable landscape dimensions"
            description="At least 1200 × 720; includes crops from 1.3:1 to 2.1:1. Turn off to inspect all artwork."
            checked={suitableOnly}
            onChange={(event) => {
              setSuitableOnly(event.currentTarget.checked);
              setVisible(12);
            }}
          />
        </Stack>
      )}
      {candidates.isLoading && <Loader aria-label="Loading candidates" />}
      {candidates.isError && <Alert color="red">{candidates.error.message}</Alert>}
      <Group justify="space-between">
        <Text
          size="sm"
          c="dimmed"
        >
          {displayed.length} images
        </Text>
        <Button
          variant="subtle"
          disabled={unavailable}
          onClick={() => {
            void candidates.refetch();
          }}
        >
          Refresh candidates
        </Button>
      </Group>
      <SimpleGrid cols={2}>
        {displayed.slice(0, visible).map((candidate) => (
          <Stack
            key={candidate.reference}
            gap={4}
          >
            <UnstyledButton
              aria-label={`Select artwork ${candidate.providerAssetId}`}
              aria-pressed={selectedReference === candidate.reference}
              disabled={unavailable}
              onClick={() => void apply(candidate)}
              className={classes.candidate}
            >
              <CandidateImage
                titleId={titleId}
                candidate={candidate}
                title={title.name}
              />
              <Text
                size="xs"
                mt="xs"
              >
                {candidate.width} × {candidate.height}
                {candidate.style ? ` · ${candidate.style}` : ''}
                {role === 'Backdrop' && candidate.width / candidate.height > 2.1
                  ? ' · Wide image · Requires cropping'
                  : ''}
              </Text>
            </UnstyledButton>
            <Button
              variant="subtle"
              color="gray"
              size="compact-xs"
              aria-label={`Hide image ${candidate.providerAssetId}`}
              disabled={unavailable || !allowHide}
              onClick={async () => {
                setPending(true);
                setHiding(true);
                setError(null);
                try {
                  if (!family) return;
                  // Hiding is a write to the artwork family, under the family revision this browser showed.
                  const response = await replaceHiddenArtworkCandidate({
                    path: {
                      titleId,
                      candidateId: candidate.candidateId,
                    },
                    headers: ifMatch(family.revision),
                    body: {
                      candidateReference: candidate.reference,
                    },
                  });
                  // A hide rotates the title's revision, so every surface of the title reloads, as after a delete.
                  if (response.response?.status === 412) {
                    setError('The artwork changed. It was reloaded; hide the image again.');
                    await invalidateTitle(cache, titleId);
                    return;
                  }
                  if (response.error || !response.data) {
                    setError(response.error?.detail ?? 'Could not hide image.');
                    return;
                  }
                  await storeArtwork(cache, titleId, response.data);
                  await candidates.refetch();
                  await invalidateTitle(cache, titleId);
                } catch {
                  setError('Could not hide image. Try again.');
                } finally {
                  setPending(false);
                  setHiding(false);
                }
              }}
            >
              Hide image
            </Button>
          </Stack>
        ))}
      </SimpleGrid>
      {!candidates.isLoading && !candidates.isError && displayed.length === 0 && (
        <Text>
          {candidates.hasNextPage
            ? 'No candidates on this page. More candidates may be available.'
            : 'No artwork matches these filters.'}
        </Text>
      )}
      {(visible < displayed.length || candidates.hasNextPage) && (
        <Button
          variant="light"
          loading={candidates.isFetchingNextPage}
          disabled={unavailable || candidates.isFetching}
          onClick={() => {
            if (visible >= displayed.length && !candidates.isFetching)
              void candidates.fetchNextPage({
                cancelRefetch: false,
              });
            setVisible((count) => count + 12);
          }}
        >
          More candidates
        </Button>
      )}
      {pending && (
        <Text
          size="sm"
          role="status"
        >
          {hiding ? 'Hiding image…' : 'Loading preview…'}
        </Text>
      )}
      {error && <Alert color="red">{error}</Alert>}
    </Stack>
  );
}

type ArtworkPanelProps = {
  titleId: string;
  title: TitleDetail;
  initialRole?: Role;
} & TitleEditorOutcome;

function ArtworkPanelContent({
  titleId,
  title,
  initialRole,
  density = 'comfortable',
  onResolved,
  onDismissed,
}: ArtworkPanelProps) {
  const cache = useQueryClient();
  const statusId = useId();
  const panelId = `${statusId}-artwork-inspector`;
  const { ref: layoutRef, wide } = useInspectorSize(750);
  const guard = useDraftGuard();
  const discardEpoch = useDiscardEpoch();
  const lastDiscardEpoch = useRef(discardEpoch);
  const artwork = useArtworkSelections(titleId);
  const selections = artwork.data?.roles;
  const saved = useSavedArtwork(titleId);
  const removeMedia = useDeleteTitleMedia();
  const [deleting, setDeleting] = useState<{
    target: SavedArtworkTarget;
    roles: Role[];
  } | null>(null);
  const [sourceChoice, setSourceChoice] = useState<{
    role: Role;
    source: string;
  }>();
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const libraryRequested = params.get('artwork') === 'library';
  const [typeFilter, setTypeFilter] = useState<LibraryTypeFilter>('all');
  const [releaseFilter, setReleaseFilter] = useState<LibraryReleaseFilter>('all');
  const [adding, setAdding] = useState<AddMediaMode | null>(null);
  /** The last provider candidate queued for a role, kept so a failed import can be retried as it was chosen. */
  const [lastImport, setLastImport] = useState<{
    role: Role;
    draft: ArtworkDraft;
    source: string;
    jobId: string;
  } | null>(null);
  const [libraryCandidates, setLibraryCandidates] = useState<Set<string>>(new Set());
  const [draft, setDraft] = useState<ArtworkDraft | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [editingMatch, setEditingMatch] = useState<TitleProviderMatch | null>(null);
  const [leavingEmpty, setLeavingEmpty] = useState<Role | null>(null);
  const leaveRoleEmpty = useLeaveArtworkEmpty(titleId);
  const matches = useProviderMatches(titleId);
  const upload = useUploadTitleMedia();
  const request = useRef<{
    key: string;
    id: string;
  } | null>(null);
  const uploaded = useRef<{
    file: File;
    mediaId: string;
  } | null>(null);
  const temporaryUrl = useRef<string | null>(null);
  useEffect(
    () => () => {
      if (temporaryUrl.current) URL.revokeObjectURL(temporaryUrl.current);
    },
    [],
  );
  const [acceptedJob, setAcceptedJob] = useState<string>();
  const [busyRole, setBusyRole] = useState<Role | null>(null);
  const [imageSaving, setImageSaving] = useState(false);
  const entries = artworkLibraryEntries(title.media ?? [], saved.data ?? [], title.artwork ?? []);
  const releases = releaseOptions(title.releases ?? []);
  const cards = entries.filter((entry) => matchesLibraryFilter(entry, typeFilter, releaseFilter));
  const roleKeys = [
    ...(initialRole
      ? [
          initialRole,
        ]
      : []),
    ...artworkRoles.filter((item) => item !== initialRole),
  ].map((item) => `role-${item}`);
  const selection = useInspectorSelection({
    param: 'image',
    ids: libraryRequested ? cards.map((entry) => entry.key) : roleKeys,
    defaultFirst: !!initialRole && !libraryRequested,
    allowUnlisted: true,
    panelId,
    canChange: () => busyRole === null && !imageSaving,
    beforeChange: (action) => void guard.discardThen(action),
  });
  const roleKey = selection.selectedId?.startsWith('role-') ? selection.selectedId.slice(5) : '';
  const inspectedRole = isArtworkRole(roleKey) ? roleKey : undefined;
  const role = inspectedRole ?? initialRole ?? 'Backdrop';
  const mode = sourceChoice?.role === role ? sourceChoice.source : 'saved';
  const providers = useArtworkProviders(titleId, role);
  const selectedEntry = entries.find((entry) => entry.key === selection.selectedId);
  const libraryView = libraryRequested || !!selectedEntry;
  const [error, setError] = useState<string | null>(null);
  const [conflicted, setConflicted] = useState(false);
  const [reloading, setReloading] = useState(false);
  const pendingJob =
    selections?.find((selection) => selection.pendingJobId)?.pendingJobId ?? acceptedJob;
  const job = useJob(pendingJob ?? undefined);
  const jobStatus = job.data
    ? artworkJobStatus({
        ...job.data,
        wasSuperseded: 'wasSuperseded' in job.data && job.data.wasSuperseded === true,
      })
    : null;
  const refresh = () => invalidateTitle(cache, titleId);
  useEffect(() => {
    if (job.data?.isTerminal) void invalidateTitle(cache, titleId);
  }, [
    job.data?.isTerminal,
    cache,
    titleId,
  ]);
  const provider = providers.data?.find((item) => item.id === mode);
  const releasePreview = () => {
    if (temporaryUrl.current) URL.revokeObjectURL(temporaryUrl.current);
    temporaryUrl.current = null;
  };
  const beginCandidate = async (candidate: ArtworkCandidate) => {
    if (busyRole) return;
    const selection = selections?.find((item) => item.role === role);
    if (!role || !selection) return;
    setBusyRole(role);
    try {
      const response = await previewArtworkCandidate({
        path: {
          titleId,
        },
        body: {
          candidateReference: candidate.reference,
        },
        parseAs: 'blob',
      });
      const data: unknown = response.data;
      if (response.error || !(data instanceof Blob)) throw new Error('Preview unavailable');
      releasePreview();
      temporaryUrl.current = URL.createObjectURL(data);
      setDraft({
        role,
        candidate,
        url: temporaryUrl.current,
        revision: selection.revision,
        focalX: 50,
        focalY: 50,
      });
      setNotice(null);
      setError(null);
    } finally {
      setBusyRole(null);
    }
  };
  const beginDraft = async (
    selectedRole: Role,
    target: SavedArtworkTarget,
    previewUrl?: string,
  ) => {
    const selection = selections?.find((item) => item.role === selectedRole);
    const asset = target.assetId ? saved.data?.find((item) => item.id === target.assetId) : null;
    const url =
      previewUrl ??
      asset?.artwork.url ??
      title.media?.find((item) => item.id === target.mediaId)?.url;
    if (!selection || !url || busyRole) return false;
    const current = target.assetId
      ? title.artwork?.find((item) => item.role === selectedRole && item.assetId === target.assetId)
      : undefined;
    releasePreview();
    setError(null);
    setNotice(null);
    setDraft({
      role: selectedRole,
      target,
      url,
      revision: selection.revision,
      focalX: current?.focalX ?? 50,
      focalY: current?.focalY ?? 50,
    });
    return true;
  };
  const assign = async (proposed: ArtworkDraft, source = mode) => {
    const { role: selectedRole, target } = proposed;
    if (busyRole) return false;
    setBusyRole(selectedRole);
    setError(null);
    try {
      if (proposed.candidate) {
        const key = JSON.stringify([
          proposed.candidate.reference,
          proposed.focalX,
          proposed.focalY,
          proposed.revision,
        ]);
        if (request.current?.key !== key)
          request.current = {
            key,
            id: createRequestId(),
          };
        // The role revision the draft started from: a stale one is 412, never a silent overwrite.
        const response = await applyArtworkCandidate({
          path: {
            titleId,
            role: selectedRole,
          },
          headers: ifMatch(proposed.revision),
          body: {
            requestId: request.current.id,
            candidateReference: proposed.candidate.reference,
            focalX: proposed.focalX,
            focalY: proposed.focalY,
          },
        });
        if (response.response?.status === 412) {
          setConflicted(true);
          setError('The selection changed. Reload the latest artwork, then choose again.');
          return false;
        }
        if (response.error || !response.data) {
          setError(
            response.error?.detail ?? 'Could not queue artwork. Retry or choose a fresh candidate.',
          );
          return false;
        }
        setAcceptedJob(response.data.jobId);
        setLastImport({
          role: selectedRole,
          draft: proposed,
          source,
          jobId: response.data.jobId,
        });
      } else {
        let mediaId = target?.mediaId;
        if (proposed.file) {
          if (uploaded.current?.file !== proposed.file) {
            const mediaType = artworkPresentation[selectedRole].mediaType.toLowerCase();
            const media = await upload.mutateAsync({
              titleId,
              file: proposed.file,
              type: mediaType,
            });
            uploaded.current = {
              file: proposed.file,
              mediaId: media.id,
            };
          }
          mediaId = uploaded.current.mediaId;
        }
        const response = await replaceTitleArtworkRole({
          path: {
            titleId,
            role: selectedRole,
          },
          headers: ifMatch(proposed.revision),
          body: {
            mode: 'Pinned',
            assetId: target?.assetId ?? null,
            mediaId: mediaId ?? null,
            focalX: proposed.focalX,
            focalY: proposed.focalY,
          },
        });
        if (response.error || !response.data) {
          const stale = response.response?.status === 412 || response.response?.status === 409;
          setConflicted(stale);
          setError(
            stale
              ? 'The selection changed. Discard this preview and choose again to review the latest artwork.'
              : (response.error?.detail ?? 'Could not select artwork.'),
          );
          await refresh();
          return false;
        }
        await storeArtwork(cache, titleId, response.data);
      }
      await refresh();
      setDraft(null);
      releasePreview();
      setNotice(
        proposed.candidate
          ? 'Artwork queued. Your saved selection stays visible until the image is ready.'
          : 'Artwork saved.',
      );
      onResolved?.();
      return true;
    } catch {
      setError(
        uploaded.current?.file === proposed.file && proposed.file
          ? 'The image is saved in your gallery, but could not be selected. Retry or cancel to keep it in the gallery.'
          : 'Could not save artwork. Try again.',
      );
      return false;
    } finally {
      setBusyRole(null);
    }
  };
  const keep = async (selectedRole: Role, assetId: string) => {
    const selection = selections?.find((item) => item.role === selectedRole);
    if (!selection) return;
    setBusyRole(selectedRole);
    setError(null);
    try {
      const response = await replaceTitleArtworkRole({
        path: {
          titleId,
          role: selectedRole,
        },
        headers: ifMatch(selection.revision),
        body: {
          mode: 'Pinned',
          assetId,
        },
      });
      if (response.error || !response.data) {
        setError(response.error?.detail ?? 'Could not keep this choice. Reload and try again.');
        await refresh();
        return;
      }
      await storeArtwork(cache, titleId, response.data);
      await refresh();
      onResolved?.();
    } catch {
      setError('Could not keep this choice. Try again.');
    } finally {
      setBusyRole(null);
    }
  };
  /** Queues the failed candidate again under the role's current revision; a fresh request ID makes it a new job. */
  const retryImport = () => {
    if (!lastImport || busyRole) return;
    const current = selections?.find((item) => item.role === lastImport.role);
    if (!current) return;
    // The failed request's ID names a finished job; retrying under it would only report that failure again.
    request.current = null;
    void assign(
      {
        ...lastImport.draft,
        revision: current.revision,
      },
      lastImport.source,
    );
  };
  /** Keeps a provider candidate in the image library for later; the role it was previewed for is unchanged. */
  const saveToLibrary = async (proposed: ArtworkDraft) => {
    const { candidate } = proposed;
    if (!candidate || busyRole) return;
    setBusyRole(proposed.role);
    setError(null);
    try {
      const response = await importGalleryArtwork({
        path: {
          titleId,
          candidateId: candidate.candidateId,
        },
        headers: ifMatch(await shownTitleRevision(cache, titleId)),
        body: {
          candidateReference: candidate.reference,
        },
      });
      if (response.response?.status === 412) {
        setError('The title changed since you opened it. It was reloaded; save the image again.');
        await refresh();
        return;
      }
      if (response.error || !response.data) {
        setError(response.error?.detail ?? 'Could not save this image to the library.');
        return;
      }
      await storeTitleRevision(
        cache,
        titleId,
        revisionFromETag(response.response?.headers.get('ETag')),
      );
      setLibraryCandidates(
        (previous) =>
          new Set([
            ...previous,
            candidate.reference,
          ]),
      );
      setNotice(
        `Saved to the image library. The ${artworkRoleLabel(proposed.role).toLowerCase()} is unchanged.`,
      );
      await Promise.all([
        refresh(),
        cache.invalidateQueries({
          queryKey: artworkKeys.candidates(titleId),
        }),
      ]);
    } catch {
      setError('Could not save this image to the library. Try again.');
    } finally {
      setBusyRole(null);
    }
  };
  const requestDelete = (target: SavedArtworkTarget, knownRoles: Role[] = []) => {
    const asset = saved.data?.find(
      (item) =>
        item.id === target.assetId || (target.mediaId && item.mediaIds.includes(target.mediaId)),
    );
    const media = title.media?.find(
      (item) => item.id === target.mediaId || asset?.mediaIds.includes(item.id),
    );
    const roles = artworkRoles.filter(
      (itemRole) =>
        knownRoles.includes(itemRole) ||
        title.artwork?.some(
          (item) =>
            item.role === itemRole &&
            ((asset && item.assetId === asset.id) ||
              (asset?.artwork.contentVersion &&
                item.contentVersion === asset.artwork.contentVersion) ||
              (media && item.url === media.url)),
        ),
    );
    setError(null);
    setDeleting({
      target,
      roles,
    });
  };
  const deleteImage = async () => {
    if (!deleting || !artwork.data) return;
    setBusyRole(role);
    setError(null);
    try {
      const { target } = deleting;
      // A saved asset is deleted from the artwork family; a title media item through the title's one media delete,
      // which also removes it from the artwork.
      const { assetId } = target;
      if (assetId) {
        const response = await deleteTitleArtworkAsset({
          path: {
            titleId,
            assetId,
          },
          headers: ifMatch(artwork.data.revision),
        });
        if (response.response?.status === 412) {
          setError(
            'The artwork changed since you opened it. It was reloaded; delete the image again.',
          );
          await refresh();
          return;
        }
        if (response.error || !response.data) {
          setError(response.error?.detail ?? 'Could not delete image. Refresh and try again.');
          return;
        }
        await storeArtwork(cache, titleId, response.data);
      } else if (target.mediaId) {
        await removeMedia.mutateAsync({
          titleId,
          mediaId: target.mediaId,
        });
      }
      await refresh();
      await cache.invalidateQueries({
        queryKey: artworkKeys.candidates(titleId),
      });
      setDeleting(null);
      setNotice('Image deleted from this title.');
    } catch (reason) {
      setError(
        reason instanceof TitleConflictError
          ? 'The title changed since you opened it. It was reloaded; delete the image again.'
          : 'Could not delete image. Try again.',
      );
    } finally {
      setBusyRole(null);
    }
  };
  const leaveEmpty = async (selectedRole: Role) => {
    const selection = selections?.find((item) => item.role === selectedRole);
    if (!selection) throw new Error('Artwork selections are still loading. Try again.');
    setBusyRole(selectedRole);
    setError(null);
    try {
      await leaveRoleEmpty.mutateAsync({
        role: selectedRole,
        revision: selection.revision,
      });
      onResolved?.();
    } finally {
      setBusyRole(null);
    }
  };
  const automatic = async (selectedRole: Role) => {
    const selection = selections?.find((item) => item.role === selectedRole);
    if (!selection) return;
    setBusyRole(selectedRole);
    setError(null);
    try {
      const response = await replaceTitleArtworkRole({
        path: {
          titleId,
          role: selectedRole,
        },
        headers: ifMatch(selection.revision),
        body: {
          mode: 'Automatic',
        },
      });
      if (response.error || !response.data) {
        setError(
          response.response?.status === 412
            ? 'The selection changed. It was reloaded; review it, then try again.'
            : (response.error?.detail ?? 'Could not return to automatic.'),
        );
        await refresh();
        return;
      }
      await storeArtwork(cache, titleId, response.data);
      await refresh();
      onResolved?.();
    } catch {
      setError('Could not return to automatic. Try again.');
    } finally {
      setBusyRole(null);
    }
  };
  const discard = () => {
    setDraft(null);
    releasePreview();
    setError(null);
    setNotice(null);
    setConflicted(false);
  };
  const reloadLatest = async () => {
    discard();
    setReloading(false);
    await refresh();
  };
  /** A stale draft cannot be saved; reloading discards it, so an open preview is confirmed first. */
  const requestReloadLatest = () => {
    if (draft) setReloading(true);
    else void reloadLatest();
  };
  useDraft(draft !== null || busyRole !== null, 'Your preview has not been applied.');

  useEffect(() => {
    if (lastDiscardEpoch.current === discardEpoch) return;
    lastDiscardEpoch.current = discardEpoch;
    if (temporaryUrl.current) URL.revokeObjectURL(temporaryUrl.current);
    temporaryUrl.current = null;
    setDraft(null);
    setError(null);
    setConflicted(false);
  }, [
    discardEpoch,
  ]);

  const chooseRole = (next: Role, source = 'saved', trigger?: HTMLElement) => {
    if (busyRole || imageSaving || draft) return;
    setSourceChoice({
      role: next,
      source,
    });
    selection.select(`role-${next}`, trigger);
    setError(null);
    setNotice(null);
  };
  /** Switches between Presentation and the Image library in one history entry, closing any open inspector. */
  const changeView = (next: string | null) => {
    if ((next === 'library') === libraryView || busyRole || imageSaving) return;
    guard.discardThen(() => {
      const updated = new URLSearchParams(params);
      updated.delete('image');
      if (next === 'library') updated.set('artwork', 'library');
      else updated.delete('artwork');
      setParams(updated, {
        replace: true,
        state: location.state,
        preventScrollReset: true,
      });
      setError(null);
      setNotice(null);
    });
  };
  const protectedRoles =
    selections
      ?.filter((item) => item.mode !== 'Automatic' || item.pendingJobId)
      .map((item) => item.role) ?? [];
  const filledRoles =
    title.artwork
      ?.filter((item) => item.url && item.fallbackReason !== 'LegacyCover')
      .map((item) => item.role) ?? [];
  const currentArtwork = title.artwork?.find((item) => item.role === role);
  const currentSelection = selections?.find((item) => item.role === role);
  const currentAsset = saved.data?.find((item) => item.id === currentArtwork?.assetId);
  const locked = busyRole !== null || imageSaving || draft !== null;
  const importRunning = !!pendingJob && !job.data?.isTerminal;
  const failedImportRole =
    lastImport && lastImport.jobId === pendingJob && job.data?.phase === 'Failed'
      ? lastImport.role
      : undefined;
  const roleState = (itemRole: Role) =>
    rolePresentationState(
      selections?.find((item) => item.role === itemRole),
      title.artwork?.find((item) => item.role === itemRole),
      {
        failedToLoad: artwork.isError,
        importing: importRunning && lastImport?.role === itemRole,
      },
    );
  const status = (itemRole: Role) => roleStateLabel(roleState(itemRole));
  const roleInspector = inspectedRole && (
    <InspectorPanel
      id={panelId}
      title={`${artworkRoleLabel(role)} artwork`}
      onClose={selection.close}
    >
      <Stack gap="md">
        <ArtworkComposer
          key={role}
          title={title}
          role={role}
          draft={draft}
          onChange={setDraft}
          busy={busyRole !== null}
          error={error}
          onCancel={() => {
            discard();
            onDismissed?.();
          }}
          onSave={(value) => void assign(value)}
          onReloadLatest={conflicted ? requestReloadLatest : undefined}
          onSaveToLibrary={(value) => void saveToLibrary(value)}
          savedToLibrary={!!draft?.candidate && libraryCandidates.has(draft.candidate.reference)}
        />
        <Group justify="space-between">
          <Text
            size="sm"
            c="dimmed"
          >
            {status(role)}
            {currentAsset ? ` · ${artworkSourceLabel(currentAsset.sourceId)}` : ''}
          </Text>
          <Menu withinPortal>
            <Menu.Target>
              <ActionIcon
                variant="subtle"
                color="gray"
                aria-label={`${artworkRoleLabel(role)} actions`}
                disabled={!currentSelection || locked}
              >
                <IconDotsVertical size={18} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              {(role === 'Hero' || role === 'Backdrop') && currentArtwork?.assetId && (
                <Menu.Item
                  onClick={() => {
                    if (currentArtwork.assetId)
                      void beginDraft(
                        role,
                        {
                          assetId: currentArtwork.assetId,
                        },
                        currentArtwork.url ?? undefined,
                      );
                  }}
                >
                  Adjust focal point
                </Menu.Item>
              )}
              {currentArtwork?.assetId && currentSelection?.mode === 'Automatic' && (
                <Menu.Item
                  onClick={() => {
                    if (currentArtwork.assetId) void keep(role, currentArtwork.assetId);
                  }}
                >
                  Keep this choice
                </Menu.Item>
              )}
              {currentArtwork?.assetId && (
                <Menu.Item
                  color="red"
                  onClick={() => {
                    if (currentArtwork.assetId)
                      requestDelete(
                        {
                          assetId: currentArtwork.assetId,
                        },
                        [
                          role,
                        ],
                      );
                  }}
                >
                  Delete image
                </Menu.Item>
              )}
              {currentSelection?.mode !== 'Empty' && (
                <Menu.Item onClick={() => setLeavingEmpty(role)}>Leave empty</Menu.Item>
              )}
              <Menu.Item
                disabled={!currentSelection?.pendingJobId && currentSelection?.mode === 'Automatic'}
                onClick={() => void automatic(role)}
              >
                Return {artworkRoleLabel(role).toLowerCase()} to automatic
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>
        <div className={classes.sourcePicker}>
          <SegmentedControl
            aria-label="Image source"
            size="xs"
            value={mode}
            onChange={(source) =>
              setSourceChoice({
                role,
                source,
              })
            }
            disabled={busyRole !== null}
            data={[
              {
                value: 'saved',
                label: 'Saved',
              },
              ...(providers.data
                ?.filter(
                  (item) =>
                    item.isAvailable && item.roles.some((capability) => capability.role === role),
                )
                .map((item) => ({
                  value: item.id,
                  label: item.name,
                })) ?? []),
              {
                value: 'custom',
                label: 'Upload',
              },
            ]}
          />
        </div>
        {mode === 'saved' && (
          <>
            {saved.isLoading && (
              <Loader
                size="sm"
                aria-label="Loading saved artwork"
              />
            )}
            {saved.isError && (
              <Alert color="red">
                Could not load saved artwork.{' '}
                <Button onClick={() => void saved.refetch()}>Retry</Button>
              </Alert>
            )}
            <MediaGallery
              onDelete={requestDelete}
              key={role}
              titleId={titleId}
              media={title.media ?? []}
              saved={saved.data ?? []}
              artwork={title.artwork ?? []}
              role={role}
              inlineSelection
              onAssign={beginDraft}
              busy={busyRole !== null || !selections}
            />
          </>
        )}
        {mode === 'custom' && (
          <FileInput
            key={`${role}:${draft?.url ?? 'empty'}`}
            label={`${artworkRoleLabel(role)} image`}
            placeholder="Select image"
            accept="image/jpeg,image/png,image/webp"
            disabled={busyRole !== null}
            onChange={(file) => {
              const selection = selections?.find((item) => item.role === role);
              if (!file || !selection) return;
              if (
                ![
                  'image/jpeg',
                  'image/png',
                  'image/webp',
                ].includes(file.type) ||
                file.size > 32 * 1024 * 1024
              ) {
                setError('Choose a JPEG, PNG, or WebP image under 32 MB.');
                return;
              }
              releasePreview();
              temporaryUrl.current = URL.createObjectURL(file);
              uploaded.current = null;
              setDraft({
                role,
                file,
                url: temporaryUrl.current,
                revision: selection.revision,
                focalX: 50,
                focalY: 50,
              });
              setError(null);
              setNotice(null);
            }}
          />
        )}
        {providers.isError && (
          <Alert color="red">
            Could not load providers.{' '}
            <Button onClick={() => void providers.refetch()}>Retry</Button>
          </Alert>
        )}
        {provider?.gameId && (
          <Stack>
            <div>
              <Text
                size="xs"
                c="dimmed"
              >
                Artwork matched to
              </Text>
              <Group justify="space-between">
                <Anchor
                  href={provider.gameUrl ?? undefined}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  {provider.gameName ?? provider.name}
                </Anchor>
                {matches.data?.find((item) => item.providerId === provider.id) && (
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    disabled={locked}
                    onClick={() => {
                      const match = matches.data?.find((item) => item.providerId === provider.id);
                      if (match) setEditingMatch(match);
                    }}
                  >
                    Change match
                  </Button>
                )}
              </Group>
            </div>
            <CandidateBrowser
              key={`${role}:${provider.id}:${provider.gameId}`}
              titleId={titleId}
              title={title}
              role={role}
              provider={provider}
              gameId={provider.gameId}
              selectedReference={draft?.candidate?.reference}
              disabled={busyRole !== null}
              allowHide={!draft}
              onChoose={beginCandidate}
            />
            {draft?.candidate && (
              <Text size="xs">
                {draft.candidate.attribution && `By ${draft.candidate.attribution} · `}
                <Anchor
                  href={draft.candidate.sourcePageUrl}
                  target="_blank"
                  rel="noreferrer"
                >
                  View on {provider.name}
                </Anchor>
              </Text>
            )}
          </Stack>
        )}
        {!providers.isPending &&
          !providers.isError &&
          !providers.data?.some(
            (item) => item.isAvailable && item.roles.some((capability) => capability.role === role),
          ) && (
            <Stack gap="xs">
              <Text
                size="sm"
                c="dimmed"
              >
                No linked provider is available for this role. Choose a saved image, upload one, or
                review a provider match.
              </Text>
              {matches.data
                ?.filter((item) => item.capabilities.includes('artwork'))
                .map((item) => (
                  <Button
                    key={item.providerId}
                    variant="light"
                    disabled={locked || !item.isAvailable}
                    onClick={() => {
                      // Once matched, the editor returns to this provider's candidates.
                      setSourceChoice({
                        role,
                        source: item.providerId,
                      });
                      setEditingMatch(item);
                    }}
                  >
                    Match {item.providerName}
                  </Button>
                ))}
              <Anchor
                size="sm"
                href="/settings"
              >
                Provider settings
              </Anchor>
            </Stack>
          )}
      </Stack>
    </InspectorPanel>
  );

  const imageInspector = selectedEntry && (
    <ArtworkImageInspector
      key={selectedEntry.key}
      id={panelId}
      titleId={titleId}
      entry={selectedEntry}
      artwork={title.artwork ?? []}
      selections={selections}
      releases={releases}
      disabled={busyRole !== null}
      onClose={selection.close}
      onDelete={() => requestDelete(selectedEntry.target, selectedEntry.roles)}
      onSaving={setImageSaving}
      onSaved={() => {
        setNotice('Changes saved.');
        onResolved?.();
      }}
    />
  );
  const filtered = typeFilter !== 'all' || releaseFilter !== 'all';

  return (
    <Stack
      className={classes.workspace}
      data-density={density}
      ref={layoutRef}
      gap="md"
    >
      <div className={classes.header}>
        <div>
          <Title
            order={3}
            className={classes.heading}
          >
            Artwork
          </Title>
          <Text
            size="sm"
            c="dimmed"
          >
            {libraryView
              ? `${entries.length} ${entries.length === 1 ? 'item' : 'items'} in the library`
              : `${filledRoles.length} of ${artworkRoles.length} roles filled`}
          </Text>
        </div>
        {libraryView && (
          <Group gap="xs">
            <Button
              {...workspaceActionProps}
              variant="light"
              leftSection={<IconUpload size={16} />}
              disabled={locked}
              onClick={() => setAdding('upload')}
            >
              Upload files
            </Button>
            <Button
              {...workspaceActionProps}
              leftSection={<IconPlus size={16} />}
              disabled={locked}
              onClick={() => setAdding('providers')}
            >
              Collect from providers
            </Button>
          </Group>
        )}
      </div>
      <Tabs
        value={libraryView ? 'library' : 'presentation'}
        onChange={changeView}
        classNames={{
          list: workspaceClasses.tabsList,
          tab: workspaceClasses.tab,
        }}
      >
        <Tabs.List>
          <Tabs.Tab
            value="presentation"
            disabled={locked}
          >
            Presentation
          </Tabs.Tab>
          <Tabs.Tab
            value="library"
            disabled={locked}
          >
            Image library
          </Tabs.Tab>
        </Tabs.List>
      </Tabs>
      {artwork.isError && (
        <Alert color="red">
          Could not load selection state.{' '}
          <Button
            variant="subtle"
            onClick={() => void artwork.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {notice && (
        <Text
          size="sm"
          role="status"
        >
          {notice}
        </Text>
      )}
      {!libraryView && (
        <>
          <ArtworkAcquisition
            titleId={titleId}
            resolvedRoles={[
              ...filledRoles,
              ...protectedRoles,
            ]}
            disabled={locked}
            onReviewBackdrop={() => chooseRole('Backdrop', 'igdb')}
          />
          {jobStatus && !failedImportRole && (
            <Alert
              color={jobStatus.color}
              title={jobStatus.title}
            >
              {jobStatus.message}
              <Anchor
                href={`/jobs/${pendingJob}`}
                ml="xs"
              >
                View job
              </Anchor>
            </Alert>
          )}
          {error && !inspectedRole && !deleting && (
            <Alert
              color="red"
              role="alert"
            >
              {error}
            </Alert>
          )}
          <InspectorLayout
            wide={wide}
            narrow="stack"
            width="27rem"
            inspector={roleInspector || undefined}
          >
            <section
              aria-label="Presentation roles"
              className={classes.roleGrid}
            >
              {artworkRoles.map((itemRole) => {
                const image = title.artwork?.find((item) => item.role === itemRole);
                const asset = saved.data?.find((item) => item.id === image?.assetId);
                return (
                  <PresentationRoleCard
                    key={itemRole}
                    role={itemRole}
                    artwork={image}
                    state={roleState(itemRole)}
                    source={asset ? artworkSourceLabel(asset.sourceId) : undefined}
                    selected={inspectedRole === itemRole}
                    panelId={panelId}
                    disabled={locked || !selections}
                    failed={failedImportRole === itemRole}
                    manual={
                      selections?.find((item) => item.role === itemRole)?.mode !== 'Automatic' &&
                      !!selections
                    }
                    onEdit={(trigger) => chooseRole(itemRole, 'saved', trigger)}
                    onAutomatic={() => void automatic(itemRole)}
                    onRetry={retryImport}
                    onChooseAnother={(trigger) => {
                      setLastImport(null);
                      chooseRole(itemRole, lastImport?.source ?? 'saved', trigger);
                    }}
                  />
                );
              })}
            </section>
          </InspectorLayout>
        </>
      )}
      {libraryView && (
        <InspectorLayout
          wide={wide}
          narrow="stack"
          width="25rem"
          inspector={imageInspector || undefined}
        >
          <Stack gap="sm">
            <Group
              gap="sm"
              align="end"
              className={classes.libraryToolbar}
            >
              <NativeSelect
                label="Type"
                value={typeFilter}
                disabled={locked}
                data={libraryTypeOptions(entries)}
                onChange={(event) => setTypeFilter(event.currentTarget.value)}
              />
              <NativeSelect
                label="Release"
                value={releaseFilter}
                disabled={locked}
                data={[
                  {
                    value: 'all',
                    label: 'All releases',
                  },
                  {
                    value: 'title',
                    label: 'Title-level',
                  },
                  ...releases.map((release) => ({
                    value: release.value,
                    label: release.label,
                  })),
                ]}
                onChange={(event) => setReleaseFilter(event.currentTarget.value)}
              />
              {filtered && (
                <Button
                  variant="subtle"
                  size="compact-sm"
                  disabled={locked}
                  onClick={() => {
                    setTypeFilter('all');
                    setReleaseFilter('all');
                  }}
                >
                  Clear filters
                </Button>
              )}
            </Group>
            {saved.isLoading && !entries.length && (
              <Loader
                size="sm"
                aria-label="Loading saved artwork"
              />
            )}
            {saved.isError && (
              <Alert color="red">
                Could not load the complete artwork library.{' '}
                <Button
                  variant="subtle"
                  onClick={() => void saved.refetch()}
                >
                  Retry
                </Button>
              </Alert>
            )}
            {!cards.length && !saved.isLoading && !saved.isError && (
              <Stack
                gap="xs"
                align="center"
                ta="center"
                className={classes.emptyGrid}
              >
                <IconPhoto size={24} />
                <Text
                  size="sm"
                  className="romd-card-title"
                >
                  {filtered ? 'Nothing matches these filters' : 'The library is empty'}
                </Text>
                <Text
                  size="sm"
                  c="dimmed"
                >
                  {filtered
                    ? 'Choose another type or release.'
                    : 'Collect images from a provider, or upload scans and manuals.'}
                </Text>
              </Stack>
            )}
            {cards.length > 0 && (
              <fieldset
                className={classes.galleryGrid}
                aria-label="Image library"
                onKeyDown={selection.onListKeyDown}
              >
                {cards.map((entry) => {
                  const document = isDocumentType(entry.name);
                  const placement = placementLabel(entry.media, releases);
                  return (
                    <div
                      key={entry.key}
                      className={classes.galleryCard}
                      {...selection.rowProps(entry.key)}
                    >
                      <UnstyledButton
                        data-inspector-trigger
                        className={classes.galleryCardButton}
                        aria-controls={selection.selectedId === entry.key ? panelId : undefined}
                        aria-expanded={selection.selectedId === entry.key}
                        aria-label={`Preview ${typeLabel(entry.name).toLowerCase()} from ${artworkSourceLabel(entry.source)}${entry.media?.catalogReleaseId || entry.media?.region ? `, ${placement}` : ''}`}
                        disabled={locked}
                        onClick={(event) => selection.select(entry.key, event.currentTarget)}
                      >
                        <ArtworkSurface
                          background={entry.name === 'Logo' ? 'checkerboard' : 'dark'}
                        >
                          {document && entry.media ? (
                            <div className={classes.thumbnail}>
                              <ManualCover
                                titleId={titleId}
                                mediaId={entry.media.id}
                                width={96}
                                label="Manual cover"
                              />
                            </div>
                          ) : (
                            <Image
                              className={classes.thumbnail}
                              src={entry.url || undefined}
                              alt=""
                              loading="lazy"
                              fit="contain"
                            />
                          )}
                        </ArtworkSurface>
                        <Stack
                          className={classes.cardDetails}
                          gap={4}
                        >
                          <Text
                            size="sm"
                            className="romd-card-title"
                          >
                            {typeLabel(entry.name)}
                          </Text>
                          <Text
                            size="xs"
                            c="dimmed"
                            className={classes.sourceLabel}
                          >
                            {[
                              artworkSourceLabel(entry.source),
                              entry.media && placement,
                            ]
                              .filter(Boolean)
                              .join(' · ')}
                          </Text>
                          {(entry.roles.length > 0 || entry.media?.isPreferred) && (
                            <Group gap={4}>
                              {entry.media?.isPreferred && (
                                <Badge
                                  size="xs"
                                  variant="light"
                                  color="grape"
                                >
                                  Preferred
                                </Badge>
                              )}
                              {entry.roles.map((itemRole) => (
                                <Badge
                                  key={itemRole}
                                  size="xs"
                                  variant="light"
                                >
                                  {artworkRoleLabel(itemRole)}
                                </Badge>
                              ))}
                            </Group>
                          )}
                        </Stack>
                      </UnstyledButton>
                    </div>
                  );
                })}
              </fieldset>
            )}
          </Stack>
        </InspectorLayout>
      )}
      <Modal
        opened={deleting !== null}
        onClose={() => {
          if (!busyRole) {
            setDeleting(null);
            setError(null);
          }
        }}
        title="Delete image?"
      >
        <Stack>
          <Text>
            This removes the image and its retained copies from this title. It will not be
            automatically chosen again.
          </Text>
          <Text>
            {deleting?.roles.length
              ? `Currently used for ${deleting.roles.map(artworkRoleLabel).join(', ')}. These roles will use other saved artwork automatically, or show their empty fallback when none is available.`
              : 'This image is not currently used in the presentation.'}
          </Text>
          {error && <Alert color="red">{error}</Alert>}
          <Group justify="flex-end">
            <Button
              variant="default"
              disabled={busyRole !== null}
              onClick={() => {
                setDeleting(null);
                setError(null);
              }}
            >
              Cancel
            </Button>
            <Button
              color="red"
              loading={busyRole !== null}
              onClick={() => void deleteImage()}
            >
              Delete image
            </Button>
          </Group>
        </Stack>
      </Modal>
      <Modal
        opened={reloading}
        onClose={() => setReloading(false)}
        title="Reload the latest artwork?"
        centered
      >
        <Stack>
          <Text>Your preview has not been applied and will be discarded.</Text>
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => setReloading(false)}
            >
              Keep editing
            </Button>
            <Button
              color="red"
              onClick={() => void reloadLatest()}
            >
              Reload latest
            </Button>
          </Group>
        </Stack>
      </Modal>
      <DecisionConfirmation
        copy={leavingEmpty ? leaveEmptyConfirmation(leavingEmpty) : undefined}
        onCancel={() => setLeavingEmpty(null)}
        onConfirm={() => (leavingEmpty ? leaveEmpty(leavingEmpty) : Promise.resolve())}
        onConfirmed={() => setLeavingEmpty(null)}
      />
      <AddArtwork
        key={adding ?? 'closed'}
        opened={adding !== null}
        onClose={() => setAdding(null)}
        titleId={titleId}
        titleName={title.name}
        initialMode={adding ?? 'providers'}
        releases={releases}
      />
      {editingMatch && (
        <ProviderMatchPicker
          titleId={titleId}
          titleName={title.name}
          row={editingMatch}
          onClose={() => setEditingMatch(null)}
        />
      )}
    </Stack>
  );
}

export function ArtworkPanel(props: ArtworkPanelProps) {
  const { user } = useAuth();
  if (!user?.roles.some((value) => value === 'Admin' || value === 'Manager'))
    return (
      <MediaGallery
        titleId={props.titleId}
        media={props.title.media ?? []}
        artwork={props.title.artwork ?? []}
      />
    );
  return <ArtworkPanelContent {...props} />;
}
