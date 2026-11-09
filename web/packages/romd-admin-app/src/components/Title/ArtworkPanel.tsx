import {
  ActionIcon,
  Alert,
  Anchor,
  Box,
  Button,
  FileInput,
  Group,
  Loader,
  Menu,
  Modal,
  Radio,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Text,
  Title,
  UnstyledButton,
} from '@mantine/core';
import type { ArtworkCandidate, ArtworkProvider, TitleProviderMatch } from '@romd/admin-api-client';
import {
  applyArtworkCandidate,
  deleteTitleArtworkAsset,
  listTitleArtworkCandidates,
  previewArtworkCandidate,
  replaceHiddenArtworkCandidate,
  replaceTitleArtworkRole,
} from '@romd/admin-api-client';
import { IconDotsVertical } from '@tabler/icons-react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch, TitleConflictError } from '../../hooks/api/titleConflict';
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
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import { ArtworkFrame } from '../ArtworkFrame';
import {
  artworkPresentation,
  artworkRoleLabel,
  artworkRoles,
  artworkSourceLabel,
  backdropSuitable,
  isArtworkRole,
  type ArtworkRole as Role,
} from '../artworkPresentation';
import { AddArtwork } from './AddArtwork';
import { ArtworkAcquisition } from './ArtworkAcquisition';
import { ArtworkComposer, type ArtworkDraft } from './ArtworkComposer';
import classes from './ArtworkWorkspace.module.css';
import { artworkJobStatus } from './artworkJobStatus';
import { CandidateImage } from './CandidateImage';
import { DecisionConfirmation } from './DecisionConfirmation';
import { leaveEmptyConfirmation } from './enrichmentPresentation';
import { MediaGallery, type SavedArtworkTarget } from './MediaGallery';
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
  initialRole = 'Backdrop',
  density = 'comfortable',
  onResolved,
  onDismissed,
}: ArtworkPanelProps) {
  const cache = useQueryClient();
  const statusId = useId();
  const artwork = useArtworkSelections(titleId);
  const selections = artwork.data?.roles;
  const saved = useSavedArtwork(titleId);
  const removeMedia = useDeleteTitleMedia();
  const [deleting, setDeleting] = useState<{
    target: SavedArtworkTarget;
    roles: Role[];
  } | null>(null);
  const [mode, setMode] = useState('saved');
  const [adding, setAdding] = useState(false);
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [draft, setDraft] = useState<ArtworkDraft | null>(null);
  const [role, setRole] = useState<Role>(initialRole);
  const [notice, setNotice] = useState<string | null>(null);
  const [editingMatch, setEditingMatch] = useState<TitleProviderMatch | null>(null);
  const [leavingEmpty, setLeavingEmpty] = useState<Role | null>(null);
  const leaveRoleEmpty = useLeaveArtworkEmpty(titleId);
  const matches = useProviderMatches(titleId);
  const providers = useArtworkProviders(titleId, role ?? undefined);
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
    setRole(selectedRole);
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
  const assign = async (proposed: ArtworkDraft) => {
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
  const chooseRole = (next: Role, source = 'saved') => {
    if (busyRole || draft) return;
    setRole(next);
    setMode(source);
    setError(null);
    setNotice(null);
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
  const locked = busyRole !== null || draft !== null;
  const status = (itemRole: Role) => {
    const state = selections?.find((item) => item.role === itemRole);
    const image = title.artwork?.find((item) => item.role === itemRole);
    if (!state) return 'Loading';
    if (state.pendingJobId) return 'Saving…';
    if (state.mode === 'Empty') return 'Left empty';
    if (image?.fallbackReason === 'LegacyCover') return 'Cover fallback';
    if (!image?.url) return 'Missing';
    return state.mode === 'Pinned' ? 'Selected by you' : 'Automatic';
  };
  return (
    <Stack
      className={classes.workspace}
      data-density={density}
    >
      <UnsavedChanges
        dirty={draft !== null || busyRole !== null}
        description="Your saved artwork will stay as it is. An image already submitted may still finish saving."
      />
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
            {filledRoles.length} of 4 roles filled
            {protectedRoles.includes('Backdrop') || filledRoles.includes('Backdrop')
              ? ''
              : ' · Backdrop missing'}
          </Text>
        </div>
        <Button
          variant="subtle"
          disabled={locked}
          onClick={() => setAdding(true)}
        >
          Add to library
        </Button>
      </div>
      <ArtworkAcquisition
        titleId={titleId}
        resolvedRoles={[
          ...filledRoles,
          ...protectedRoles,
        ]}
        disabled={locked}
        onReviewBackdrop={() => chooseRole('Backdrop', 'igdb')}
      />
      {artwork.isError && (
        <Alert color="red">
          Could not load selection state.{' '}
          <Button onClick={() => void artwork.refetch()}>Retry</Button>
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
      {jobStatus && (
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
      {/* One level of choice inside the Artwork tab: roles are a radio group, and each option carries its status. */}
      <Radio.Group
        label="Artwork roles"
        styles={{
          label: {
            display: 'none',
          },
        }}
        value={role}
        onChange={(value) => {
          if (isArtworkRole(value)) chooseRole(value);
        }}
      >
        <div className={classes.roleStrip}>
          {artworkRoles.map((itemRole) => {
            const artwork = title.artwork?.find((item) => item.role === itemRole);
            return (
              <Radio.Card
                key={itemRole}
                value={itemRole}
                className={classes.roleButton}
                withBorder={false}
                aria-label={artworkRoleLabel(itemRole)}
                aria-describedby={`${statusId}-${itemRole}`}
                tabIndex={role === itemRole ? 0 : -1}
                data-autofocus={role === itemRole || undefined}
                disabled={locked}
              >
                <Box className={classes.roleThumbnail}>
                  <ArtworkFrame
                    artworkRole={itemRole}
                    src={artwork?.url}
                    title={title.name}
                    fit={artwork?.fit === 'Contain' ? 'Contain' : 'Cover'}
                  />
                </Box>
                <Stack gap={2}>
                  <Text
                    size="sm"
                    fw={600}
                  >
                    {artworkRoleLabel(itemRole)}
                  </Text>
                  <Text
                    id={`${statusId}-${itemRole}`}
                    size="xs"
                    c="dimmed"
                  >
                    {status(itemRole)}
                  </Text>
                </Stack>
              </Radio.Card>
            );
          })}
        </div>
      </Radio.Group>
      {draft && (
        <Text
          size="xs"
          c="dimmed"
        >
          Apply or discard this preview before editing another role. Choosing another image replaces
          only the preview.
        </Text>
      )}
      <div className={classes.editorLayout}>
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
        />
        <aside
          className={classes.inspector}
          aria-label={`${artworkRoleLabel(role)} images`}
        >
          <Group justify="space-between">
            <Title
              order={4}
              className={classes.heading}
            >
              {artworkRoleLabel(role)}
            </Title>
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
                  disabled={
                    !currentSelection?.pendingJobId && currentSelection?.mode === 'Automatic'
                  }
                  onClick={() => void automatic(role)}
                >
                  Return {artworkRoleLabel(role).toLowerCase()} to automatic
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
          <Text
            size="sm"
            c="dimmed"
          >
            {status(role)}
            {currentAsset ? ` · ${artworkSourceLabel(currentAsset.sourceId)}` : ''}
          </Text>
          <div className={classes.sourcePicker}>
            <SegmentedControl
              aria-label="Image source"
              size="xs"
              value={mode}
              onChange={setMode}
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
              (item) =>
                item.isAvailable && item.roles.some((capability) => capability.role === role),
            ) && (
              <Stack gap="xs">
                <Text
                  size="sm"
                  c="dimmed"
                >
                  No linked provider is available for this role. Choose a saved image, upload one,
                  or review a provider match.
                </Text>
                {matches.data
                  ?.filter((item) => item.capabilities.includes('artwork'))
                  .map((item) => (
                    <Button
                      key={item.providerId}
                      variant="light"
                      disabled={locked || !item.isAvailable}
                      onClick={() => setEditingMatch(item)}
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
        </aside>
      </div>
      <details
        className={classes.library}
        onToggle={(event) => setLibraryOpen(event.currentTarget.open)}
      >
        <summary>Saved artwork library</summary>
        {libraryOpen && (
          <Stack mt="md">
            <MediaGallery
              onDelete={requestDelete}
              titleId={titleId}
              media={title.media ?? []}
              saved={saved.data ?? []}
              artwork={title.artwork ?? []}
              onAssign={beginDraft}
              busy={locked || !selections}
              onAdd={() => setAdding(true)}
            />
          </Stack>
        )}
      </details>
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
        opened={adding}
        onClose={() => setAdding(false)}
        titleId={titleId}
        titleName={title.name}
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
