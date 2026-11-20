import { Alert, Button, Group, Modal, SegmentedControl, Stack, Text } from '@mantine/core';
import type { ArtworkCandidate, TitleProviderMatch } from '@romd/admin-api-client';
import {
  applyArtworkCandidate,
  importGalleryArtwork,
  previewArtworkCandidate,
  replaceTitleArtworkRole,
} from '@romd/admin-api-client';
import { useQueries, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { invalidateTitle } from '../../../hooks/api/titleCache';
import {
  ifMatch,
  revisionFromETag,
  shownTitleRevision,
  storeTitleRevision,
} from '../../../hooks/api/titleConflict';
import {
  artworkKeys,
  storeArtwork,
  useArtworkProviders,
  useArtworkSelections,
  useSavedArtwork,
} from '../../../hooks/api/useArtwork';
import { jobQueryOptions } from '../../../hooks/api/useJobs';
import { useProviderMatches } from '../../../hooks/api/useProviderMatches';
import { useUploadTitleMedia } from '../../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../../hooks/api/useTitleDetail';
import { createRequestId } from '../../../utils/requestId';
import { useDiscardEpoch, useDraft, useDraftGuard } from '../../Administration/DraftGuard';
import {
  artworkPresentation,
  artworkRoleLabel,
  artworkRoles,
  artworkSourceLabel,
  isArtworkRole,
  type ArtworkRole as Role,
} from '../../artworkPresentation';
import { useInspectorSelection, useInspectorSize } from '../../Workspace/useInspectorSelection';
import { workspaceActionProps } from '../../Workspace/workspaceActions';
import { ArtworkAcquisition } from '../ArtworkAcquisition';
import classes from '../ArtworkWorkspace.module.css';
import { artworkJobStatus } from '../artworkJobStatus';
import type { SavedArtworkTarget } from '../artworkLibrary';
import type { FocalPoint } from '../FocalPointEditor';
import { usePreviewDevice } from '../preview/PreviewDeviceSwitcher';
import { PresentationCanvas, type RoleStatus, RoleStrip } from './PresentationCanvas';
import {
  type ArtworkDraft,
  cropProbes,
  type PresentationSurface,
  proposedArtwork,
  rolePresentationState,
  type StagedChange,
  type StagedChanges,
  stagedLabel,
  stagedRoles,
  surfaceForRole,
} from './presentationModel';
import { RoleTray } from './RoleTray';

type Outcome =
  | {
      ok: true;
      queued: boolean;
    }
  | {
      ok: false;
      message: string;
      conflict: boolean;
    };

const deviceFrameLabel = {
  desktop: 'Desktop',
  tablet: 'Tablet',
  mobile: 'Mobile',
} as const;

/**
 * Presentation: the consumer surfaces laid out as players see them, with each role clickable. Choosing an image, a
 * crop, automatic, or empty stages a change the canvas shows at once; Save changes commits every staged role, each
 * under the revision it started from. Provider imports keep the current image until the new one is ready, and a
 * failed import offers Retry and Choose another.
 */
export function PresentationStudio({
  titleId,
  title,
  initialRole,
  onResolved,
  onDismissed,
  onDelete,
  onEditMatch,
  onBusyChange,
}: {
  titleId: string;
  title: TitleDetail;
  initialRole?: Role;
  onResolved?: () => void;
  onDismissed?: () => void;
  onDelete: (target: SavedArtworkTarget, roles: Role[]) => void;
  onEditMatch: (match: TitleProviderMatch) => void;
  onBusyChange?: (busy: boolean) => void;
}) {
  const cache = useQueryClient();
  const id = useId();
  const panelId = `${id}-role-tray`;
  const discardEpoch = useDiscardEpoch();
  const { navigateWithinEditor } = useDraftGuard();
  const lastDiscardEpoch = useRef(discardEpoch);
  const artwork = useArtworkSelections(titleId);
  const selections = artwork.data?.roles;
  const saved = useSavedArtwork(titleId);
  const matches = useProviderMatches(titleId);
  const upload = useUploadTitleMedia();
  const [staged, setStaged] = useState<StagedChanges>({});
  const [sources, setSources] = useState<Partial<Record<Role, string>>>({});
  /** The last provider candidate queued for each role, kept so a failed import can be retried as it was chosen. */
  const [imports, setImports] = useState<
    Partial<
      Record<
        Role,
        {
          draft: ArtworkDraft;
          source: string;
          jobId: string;
        }
      >
    >
  >({});
  const [roleErrors, setRoleErrors] = useState<
    Partial<
      Record<
        Role,
        {
          message: string;
          conflict: boolean;
        }
      >
    >
  >({});
  const [savedRoles, setSavedRoles] = useState<readonly Role[]>([]);
  const [busy, setBusy] = useState(false);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState<{
    role: Role;
    point: FocalPoint;
  } | null>(null);
  const [comparison, setComparison] = useState<'proposed' | 'current'>('proposed');
  const [surface, setSurface] = useState<PresentationSurface>(
    initialRole ? surfaceForRole(initialRole, 'details') : 'details',
  );
  const [libraryCandidates, setLibraryCandidates] = useState<Set<string>>(new Set());
  const [reloading, setReloading] = useState(false);
  const requests = useRef<
    Partial<
      Record<
        Role,
        {
          key: string;
          id: string;
        }
      >
    >
  >({});
  const uploads = useRef<
    Partial<
      Record<
        Role,
        {
          file: File;
          mediaId: string;
        }
      >
    >
  >({});
  const previews = useRef<Partial<Record<Role, string>>>({});
  useEffect(
    () => () => {
      for (const url of Object.values(previews.current)) if (url) URL.revokeObjectURL(url);
    },
    [],
  );
  const releasePreview = (role: Role) => {
    const url = previews.current[role];
    if (url) URL.revokeObjectURL(url);
    delete previews.current[role];
  };
  const preview = (role: Role, content: Blob) => {
    releasePreview(role);
    const url = URL.createObjectURL(content);
    previews.current[role] = url;
    return url;
  };

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
    ids: roleKeys,
    defaultFirst: !!initialRole,
    panelId,
    canChange: () => !saving,
    beforeChange: navigateWithinEditor,
  });
  const selectedKey = selection.selectedId?.startsWith('role-')
    ? selection.selectedId.slice(5)
    : '';
  const role = isArtworkRole(selectedKey) ? selectedKey : undefined;
  const providers = useArtworkProviders(titleId, role ?? 'Backdrop');
  const { ref: measureRef, device, choose } = usePreviewDevice();
  const { ref: layoutRef, wide, measured } = useInspectorSize();

  const jobIds = artworkRoles.map(
    (item) =>
      imports[item]?.jobId ??
      selections?.find((value) => value.role === item)?.pendingJobId ??
      undefined,
  );
  const jobs = useQueries({
    queries: jobIds.map((jobId) => jobQueryOptions(jobId)),
  });
  const terminalJobs = jobs
    .map((query, index) => (query.data?.isTerminal ? jobIds[index] : ''))
    .join('|');
  useEffect(() => {
    if (terminalJobs.replaceAll('|', '')) void invalidateTitle(cache, titleId);
  }, [
    terminalJobs,
    cache,
    titleId,
  ]);
  const jobOf = (item: Role) => jobs[artworkRoles.indexOf(item)]?.data;
  const failed = (item: Role) => {
    const job = jobOf(item);
    return !!imports[item] && job?.phase === 'Failed';
  };
  const importing = (item: Role) => {
    const job = jobOf(item);
    return (
      !!selections?.find((value) => value.role === item)?.pendingJobId ||
      (!!imports[item] && !job?.isTerminal)
    );
  };
  const roleState = (item: Role) =>
    rolePresentationState(
      selections?.find((value) => value.role === item),
      title.artwork?.find((value) => value.role === item),
      {
        failedToLoad: artwork.isError,
        importing: importing(item),
        failed: failed(item),
      },
    );

  const count = stagedRoles(staged).length;
  useDraft(count > 0 || saving, 'Your presentation changes have not been saved.');
  useEffect(() => {
    onBusyChange?.(busy || saving);
  }, [
    busy,
    saving,
    onBusyChange,
  ]);
  // biome-ignore lint/correctness/useExhaustiveDependencies: releasePreview only reads a ref.
  useEffect(() => {
    if (lastDiscardEpoch.current === discardEpoch) return;
    lastDiscardEpoch.current = discardEpoch;
    for (const item of artworkRoles) releasePreview(item);
    setStaged({});
    setRoleErrors({});
    setLive(null);
  }, [
    discardEpoch,
  ]);
  const refresh = () => invalidateTitle(cache, titleId);
  const revisionOf = (item: Role) => selections?.find((value) => value.role === item)?.revision;
  const stage = (item: Role, change: StagedChange | undefined) => {
    setStaged((current) => {
      const next = {
        ...current,
      };
      if (change) next[item] = change;
      else delete next[item];
      return next;
    });
    setRoleErrors((current) => {
      const next = {
        ...current,
      };
      delete next[item];
      return next;
    });
    if (change) setSavedRoles((current) => current.filter((role) => role !== item));
    setComparison('proposed');
    setNotice(null);
    setError(null);
  };

  const selectRole = (item: Role, trigger?: HTMLElement) => {
    setSurface((current) =>
      item === 'Poster' &&
      current === 'details' &&
      device !== 'mobile' &&
      title.artwork?.some((image) => image.role === 'Backdrop' && image.url)
        ? 'library'
        : surfaceForRole(item, current),
    );
    selection.select(`role-${item}`, trigger);
    setNotice(null);
  };

  const chooseSaved = async (item: Role, target: SavedArtworkTarget, url?: string) => {
    const revision = revisionOf(item);
    const asset = target.assetId ? saved.data?.find((value) => value.id === target.assetId) : null;
    const imageUrl =
      url ??
      asset?.artwork.url ??
      title.media?.find((value) => value.id === target.mediaId)?.url ??
      undefined;
    if (!revision || !imageUrl || busy) return false;
    const current = target.assetId
      ? title.artwork?.find((value) => value.role === item && value.assetId === target.assetId)
      : undefined;
    releasePreview(item);
    stage(item, {
      kind: 'image',
      draft: {
        role: item,
        target,
        url: imageUrl,
        revision,
        focalX: current?.focalX ?? 50,
        focalY: current?.focalY ?? 50,
      },
    });
    return true;
  };

  const chooseCandidate = async (item: Role, candidate: ArtworkCandidate) => {
    const revision = revisionOf(item);
    if (busy || !revision) return;
    setBusy(true);
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
      stage(item, {
        kind: 'image',
        draft: {
          role: item,
          candidate,
          url: preview(item, data),
          revision,
          focalX: 50,
          focalY: 50,
        },
      });
    } finally {
      setBusy(false);
    }
  };

  const chooseFile = (item: Role, file: File) => {
    const revision = revisionOf(item);
    if (!revision) return;
    if (
      ![
        'image/jpeg',
        'image/png',
        'image/webp',
      ].includes(file.type) ||
      file.size > 32 * 1024 * 1024
    ) {
      setRoleErrors((current) => ({
        ...current,
        [item]: {
          message: 'Choose a JPEG, PNG, or WebP image under 32 MB.',
          conflict: false,
        },
      }));
      return;
    }
    delete uploads.current[item];
    stage(item, {
      kind: 'image',
      draft: {
        role: item,
        file,
        url: preview(item, file),
        revision,
        focalX: 50,
        focalY: 50,
      },
    });
  };

  /** Stages the current image under a pin with the given crop, or moves the crop of the staged image. */
  const commitFocal = (item: Role, point: FocalPoint) => {
    const change = staged[item];
    if (change?.kind === 'image') {
      stage(item, {
        kind: 'image',
        draft: {
          ...change.draft,
          ...point,
        },
      });
      return;
    }
    if (!change && selections?.find((value) => value.role === item)?.mode === 'Pinned') {
      keep(item, point);
    }
  };

  const keep = (item: Role, point?: FocalPoint) => {
    const current = title.artwork?.find((value) => value.role === item);
    const revision = revisionOf(item);
    if (!current?.assetId || !current.url || !revision) return;
    releasePreview(item);
    stage(item, {
      kind: 'image',
      draft: {
        role: item,
        target: {
          assetId: current.assetId,
        },
        url: current.url,
        revision,
        focalX: point?.focalX ?? current.focalX ?? 50,
        focalY: point?.focalY ?? current.focalY ?? 50,
      },
    });
  };

  const apply = async (item: Role, change: StagedChange): Promise<Outcome> => {
    const label = artworkRoleLabel(item).toLowerCase();
    try {
      if (change.kind !== 'image') {
        const response = await replaceTitleArtworkRole({
          path: {
            titleId,
            role: item,
          },
          headers: ifMatch(change.revision),
          body: {
            mode: change.kind === 'automatic' ? 'Automatic' : 'Empty',
          },
        });
        if (response.error || !response.data) {
          const conflict = response.response?.status === 412 || response.response?.status === 409;
          return {
            ok: false,
            conflict,
            message: conflict
              ? `The ${label} changed since you opened it. Reload the latest artwork, then decide again.`
              : (response.error?.detail ??
                (change.kind === 'automatic'
                  ? 'Could not return to automatic.'
                  : `Could not leave the ${label} empty.`)),
          };
        }
        await storeArtwork(cache, titleId, response.data);
        return {
          ok: true,
          queued: false,
        };
      }
      const { draft } = change;
      if (draft.candidate) {
        const key = JSON.stringify([
          draft.candidate.reference,
          draft.focalX,
          draft.focalY,
          draft.revision,
        ]);
        // A retried save reuses the request ID, so a request that reached the server is not queued twice.
        const request =
          requests.current[item]?.key === key
            ? requests.current[item]
            : {
                key,
                id: createRequestId(),
              };
        requests.current[item] = request;
        const response = await applyArtworkCandidate({
          path: {
            titleId,
            role: item,
          },
          headers: ifMatch(draft.revision),
          body: {
            requestId: request.id,
            candidateReference: draft.candidate.reference,
            focalX: draft.focalX,
            focalY: draft.focalY,
          },
        });
        if (response.response?.status === 412)
          return {
            ok: false,
            conflict: true,
            message: `The ${label} changed since you opened it. Reload the latest artwork, then choose again.`,
          };
        if (response.error || !response.data)
          return {
            ok: false,
            conflict: false,
            message:
              response.error?.detail ??
              'Could not queue artwork. Retry or choose a fresh candidate.',
          };
        const { jobId } = response.data;
        setImports((current) => ({
          ...current,
          [item]: {
            draft,
            source: sources[item] ?? 'saved',
            jobId,
          },
        }));
        return {
          ok: true,
          queued: true,
        };
      }
      let mediaId = draft.target?.mediaId;
      if (draft.file) {
        if (uploads.current[item]?.file !== draft.file) {
          const media = await upload.mutateAsync({
            titleId,
            file: draft.file,
            type: artworkPresentation[item].mediaType.toLowerCase(),
          });
          uploads.current[item] = {
            file: draft.file,
            mediaId: media.id,
          };
        }
        mediaId = uploads.current[item]?.mediaId;
      }
      const response = await replaceTitleArtworkRole({
        path: {
          titleId,
          role: item,
        },
        headers: ifMatch(draft.revision),
        body: {
          mode: 'Pinned',
          assetId: draft.target?.assetId ?? null,
          mediaId: mediaId ?? null,
          focalX: draft.focalX,
          focalY: draft.focalY,
        },
      });
      if (response.error || !response.data) {
        const conflict = response.response?.status === 412 || response.response?.status === 409;
        return {
          ok: false,
          conflict,
          message: conflict
            ? `The ${label} changed since you opened it. Reload the latest artwork, then choose again.`
            : (response.error?.detail ?? 'Could not select artwork.'),
        };
      }
      await storeArtwork(cache, titleId, response.data);
      return {
        ok: true,
        queued: false,
      };
    } catch {
      return {
        ok: false,
        conflict: false,
        message:
          change.kind === 'image' &&
          change.draft.file &&
          uploads.current[item]?.file === change.draft.file
            ? 'The image is saved in your library, but could not be selected. Save again to retry.'
            : `Could not save the ${label}. Try again.`,
      };
    }
  };

  const save = async () => {
    const roles = stagedRoles(staged);
    if (!roles.length || saving) return;
    setSaving(true);
    setError(null);
    setNotice(null);
    const errors: typeof roleErrors = {};
    const committed: Role[] = [];
    let queued = 0;
    for (const item of roles) {
      const change = staged[item];
      if (!change) continue;
      const outcome = await apply(item, change);
      if (outcome.ok) {
        if (outcome.queued) {
          queued += 1;
        } else {
          committed.push(item);
          releasePreview(item);
          setImports((current) => {
            const next = {
              ...current,
            };
            delete next[item];
            return next;
          });
        }
        setStaged((current) => {
          const next = {
            ...current,
          };
          delete next[item];
          return next;
        });
      } else {
        errors[item] = {
          message: outcome.message,
          conflict: outcome.conflict,
        };
      }
    }
    await refresh();
    setRoleErrors(errors);
    setSavedRoles((current) => [
      ...new Set([
        ...current,
        ...committed,
      ]),
    ]);
    setSaving(false);
    setLive(null);
    const failedRoles = artworkRoles.filter((item) => errors[item]);
    if (failedRoles.length) {
      setError(
        `${committed.length ? `${committed.length} saved. ` : ''}${queued ? `${queued} importing. ` : ''}Could not save ${failedRoles
          .map((item) => artworkRoleLabel(item).toLowerCase())
          .join(', ')}: ${failedRoles.map((item) => errors[item]?.message).join(' ')}`,
      );
      if (failedRoles.length === 1 && failedRoles[0] !== role) selectRole(failedRoles[0]);
      return;
    }
    setNotice(
      queued
        ? 'Saved. New images import in the background; players keep the current ones until they are ready.'
        : 'Presentation saved.',
    );
    onResolved?.();
  };

  /** Queues the failed candidate again under the role's current revision; a fresh request ID makes it a new job. */
  const retryImport = async (item: Role) => {
    const previous = imports[item];
    const revision = revisionOf(item);
    if (!previous || !revision || busy) return;
    // The failed request's ID names a finished job; retrying under it would only report that failure again.
    delete requests.current[item];
    setBusy(true);
    setError(null);
    const outcome = await apply(item, {
      kind: 'image',
      draft: {
        ...previous.draft,
        revision,
      },
    });
    setBusy(false);
    if (!outcome.ok) setError(outcome.message);
    else setNotice('Import queued again. The current image stays visible until it is ready.');
    await refresh();
  };

  const chooseAnother = (item: Role) => {
    const previous = imports[item];
    setImports((current) => {
      const next = {
        ...current,
      };
      delete next[item];
      return next;
    });
    setSources((current) => ({
      ...current,
      [item]: previous?.source ?? 'saved',
    }));
    selectRole(item);
  };

  /** Keeps a provider candidate in the image library for later; the role it was previewed for is unchanged. */
  const saveToLibrary = async (item: Role, candidate: ArtworkCandidate) => {
    if (busy) return;
    setBusy(true);
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
        `Saved to the image library. The ${artworkRoleLabel(item).toLowerCase()} is unchanged.`,
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
      setBusy(false);
    }
  };

  const discardAll = () => {
    for (const item of artworkRoles) releasePreview(item);
    setStaged({});
    setRoleErrors({});
    setLive(null);
    setError(null);
    setNotice(null);
    onDismissed?.();
  };
  const conflicted = artworkRoles.filter((item) => roleErrors[item]?.conflict);
  const reloadLatest = async () => {
    setReloading(false);
    for (const item of conflicted) {
      releasePreview(item);
      stage(item, undefined);
    }
    setError(null);
    await refresh();
  };

  const showCurrent = comparison === 'current' && count > 0;
  const shown: TitleDetail = showCurrent
    ? title
    : {
        ...title,
        artwork: proposedArtwork(title.artwork ?? [], staged, live ?? undefined),
      };
  const proposedTitle: TitleDetail = {
    ...title,
    artwork: proposedArtwork(title.artwork ?? [], staged, live ?? undefined),
  };
  const protectedRoles =
    selections
      ?.filter((item) => item.mode !== 'Automatic' || item.pendingJobId)
      .map((item) => item.role) ?? [];
  const filledRoles =
    title.artwork
      ?.filter((item) => item.url && item.fallbackReason !== 'LegacyCover')
      .map((item) => item.role) ?? [];
  const currentAsset = (item: Role) =>
    saved.data?.find(
      (value) => value.id === title.artwork?.find((image) => image.role === item)?.assetId,
    );
  const jobStatus = (item: Role) => {
    const job = jobOf(item);
    const jobId = jobIds[artworkRoles.indexOf(item)];
    return job && jobId
      ? {
          ...artworkJobStatus({
            ...job,
            wasSuperseded: 'wasSuperseded' in job && job.wasSuperseded === true,
          }),
          jobId,
        }
      : undefined;
  };
  const status = (item: Role): RoleStatus => {
    const state = roleState(item);
    const job = jobOf(item);
    const published = job?.phase === 'Completed' && !('wasSuperseded' in job && job.wasSuperseded);
    return {
      state,
      staged: staged[item] ? stagedLabel(staged[item]) : undefined,
      result:
        roleErrors[item] || state === 'failed'
          ? 'Needs attention'
          : state !== 'importing' && (savedRoles.includes(item) || published)
            ? 'Saved'
            : undefined,
    };
  };
  const locked = busy || saving;
  const stagedChange = role ? staged[role] : undefined;
  const stagedCandidate = stagedChange?.kind === 'image' ? stagedChange.draft.candidate : undefined;

  return (
    <Stack
      gap="md"
      ref={measureRef}
      className={classes.studio}
    >
      <ArtworkAcquisition
        titleId={titleId}
        resolvedRoles={[
          ...filledRoles,
          ...protectedRoles,
        ]}
        disabled={locked || count > 0}
        complete={artworkRoles.every((item) => filledRoles.includes(item))}
        onReviewBackdrop={() => {
          setSources((current) => ({
            ...current,
            Backdrop: 'igdb',
          }));
          selectRole('Backdrop');
        }}
      />
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
      {error && (
        <Alert
          color="red"
          role="alert"
        >
          {error}
        </Alert>
      )}
      <RoleStrip
        artwork={shown.artwork ?? []}
        status={status}
        selectedRole={role}
        panelId={panelId}
        disabled={locked || !selections}
        onSelect={selectRole}
      />
      <div
        ref={layoutRef}
        className={classes.presentationLayout}
        data-editing={role || undefined}
      >
        <PresentationCanvas
          title={shown}
          surface={surface}
          onSurfaceChange={setSurface}
          device={device}
          onDeviceChange={choose}
          status={status}
          selectedRole={role}
          panelId={panelId}
          disabled={locked || !selections}
          onSelect={(item, trigger) => selectRole(item, trigger)}
          comparison={
            count > 0 ? (
              <SegmentedControl
                aria-label="Artwork comparison"
                size="xs"
                value={comparison}
                onChange={(value) => setComparison(value === 'current' ? 'current' : 'proposed')}
                data={[
                  {
                    value: 'current',
                    label: 'Saved',
                  },
                  {
                    value: 'proposed',
                    label: 'Draft',
                  },
                ]}
              />
            ) : undefined
          }
        />
        {role && (
          <div data-inspector-stacked={(measured && !wide) || undefined}>
            <RoleTray
              key={role}
              panelId={panelId}
              titleId={titleId}
              title={title}
              proposedTitle={proposedTitle}
              role={role}
              status={status(role)}
              staged={stagedChange}
              selection={selections?.find((item) => item.role === role)}
              currentArtwork={title.artwork?.find((item) => item.role === role)}
              currentSource={(() => {
                const asset = currentAsset(role);
                return asset ? artworkSourceLabel(asset.sourceId) : undefined;
              })()}
              activeFrame={
                role === 'Poster'
                  ? cropProbes(role).find((probe) => probe.surface === surface)?.label
                  : deviceFrameLabel[device]
              }
              onFrameChange={(label) => {
                const probe = cropProbes(role).find((frame) => frame.label === label);
                if (!probe) return;
                setSurface(probe.surface);
                if (role !== 'Poster' || probe.surface === 'details') choose(probe.device);
              }}
              source={sources[role] ?? 'saved'}
              onSourceChange={(source) =>
                setSources((current) => ({
                  ...current,
                  [role]: source,
                }))
              }
              providers={providers.data}
              providersPending={providers.isPending}
              providersError={providers.isError}
              onRetryProviders={() => void providers.refetch()}
              matches={matches.data}
              onEditMatch={(match, source) => {
                if (source)
                  setSources((current) => ({
                    ...current,
                    [role]: source,
                  }));
                onEditMatch(match);
              }}
              saved={saved.data}
              savedLoading={saved.isLoading}
              savedError={saved.isError}
              onRetrySaved={() => void saved.refetch()}
              busy={locked}
              error={roleErrors[role]?.message}
              importing={importing(role)}
              failed={failed(role)}
              jobStatus={jobStatus(role)}
              onRetryImport={() => void retryImport(role)}
              onChooseAnother={() => chooseAnother(role)}
              onChooseSaved={(target, url) => chooseSaved(role, target, url)}
              onChooseCandidate={(candidate) => chooseCandidate(role, candidate)}
              onChooseFile={(file) => chooseFile(role, file)}
              onFocalLive={(point) => {
                if (point) setComparison('proposed');
                setLive(
                  point
                    ? {
                        role,
                        point,
                      }
                    : null,
                );
              }}
              onFocalCommit={(point) => commitFocal(role, point)}
              onAutomatic={() => {
                const revision = revisionOf(role);
                if (revision)
                  stage(role, {
                    kind: 'automatic',
                    revision,
                  });
              }}
              onEmpty={() => {
                const revision = revisionOf(role);
                if (revision)
                  stage(role, {
                    kind: 'empty',
                    revision,
                  });
              }}
              onKeep={() => keep(role)}
              onUndo={() => {
                releasePreview(role);
                stage(role, undefined);
              }}
              onDelete={onDelete}
              onSaveToLibrary={
                stagedCandidate ? () => void saveToLibrary(role, stagedCandidate) : undefined
              }
              savedToLibrary={!!stagedCandidate && libraryCandidates.has(stagedCandidate.reference)}
              onClose={selection.close}
            />
          </div>
        )}
      </div>
      {(count > 0 || saving) && (
        <section
          aria-label="Unsaved presentation changes"
          className={classes.saveBar}
        >
          <Text
            size="sm"
            className={classes.saveBarSummary}
          >
            {count} unsaved {count === 1 ? 'change' : 'changes'}:{' '}
            {stagedRoles(staged)
              .map((item) => {
                const change = staged[item];
                return `${artworkRoleLabel(item)} (${change ? stagedLabel(change).toLowerCase() : ''})`;
              })
              .join(', ')}
          </Text>
          <Group
            gap="xs"
            ms="auto"
          >
            {conflicted.length > 0 && (
              <Button
                {...workspaceActionProps}
                variant="light"
                disabled={saving}
                onClick={() => setReloading(true)}
              >
                Reload latest
              </Button>
            )}
            <Button
              {...workspaceActionProps}
              variant="default"
              disabled={saving}
              onClick={discardAll}
            >
              Discard
            </Button>
            <Button
              {...workspaceActionProps}
              loading={saving}
              disabled={busy}
              onClick={() => void save()}
            >
              Save changes
            </Button>
          </Group>
        </section>
      )}
      <Modal
        opened={reloading}
        onClose={() => setReloading(false)}
        title="Reload the latest artwork?"
        centered
      >
        <Stack>
          <Text>
            Your unsaved changes to{' '}
            {conflicted.map((item) => artworkRoleLabel(item).toLowerCase()).join(', ')} will be
            discarded.
          </Text>
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
    </Stack>
  );
}
