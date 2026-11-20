import {
  Alert,
  Anchor,
  Button,
  Group,
  Image,
  NativeSelect,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import type {
  ArtworkMode,
  ArtworkSelectionState,
  ResolvedArtwork,
  TitleMediaClassification,
} from '@romd/admin-api-client';
import { replaceTitleArtworkRole } from '@romd/admin-api-client';
import { IconBook, IconDownload } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch, TitleConflictError } from '../../hooks/api/titleConflict';
import { storeArtwork } from '../../hooks/api/useArtwork';
import { useClassifyTitleMedia } from '../../hooks/api/useTitleActions';
import { formatBytes } from '../../utils/format';
import { useDiscardEpoch, useDraft, useDraftGuard } from '../Administration/DraftGuard';
import { ArtworkSurface } from '../ArtworkSurface';
import {
  type ArtworkRole,
  artworkRoleLabel,
  artworkRoles,
  artworkSourceLabel,
  isArtworkRole,
} from '../artworkPresentation';
import {
  compatibleMediaTypes,
  isDocumentType,
  isPackagingType,
  mediaTypeLabel,
} from '../mediaTypes';
import { EditorFooter } from '../Workspace/EditorFooter';
import { InspectorPanel } from '../Workspace/InspectorPanel';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import classes from './ArtworkWorkspace.module.css';
import { type ArtworkLibraryEntry, artworkTargetForRole } from './artworkLibrary';
import { typeLabel } from './BatchArtworkUpload';
import { type FocalPoint, FocalPointEditor } from './FocalPointEditor';
import { ManualCover, ManualViewer } from './ManualViewer';
import { placementLabel, type ReleaseOption } from './mediaLibrary';

type RoleDraft = FocalPoint & {
  mode: ArtworkMode;
  revision: string;
};
type Drafts = Partial<Record<ArtworkRole, RoleDraft>>;

interface ArtworkImageInspectorProps {
  id: string;
  titleId: string;
  entry: ArtworkLibraryEntry;
  artwork: ResolvedArtwork[];
  selections?: ArtworkSelectionState[];
  /** The title's canonical releases, offered as placements. */
  releases?: ReleaseOption[];
  disabled: boolean;
  onClose: () => void;
  onDelete: () => void;
  onSaved: () => void;
  onSaving: (value: boolean) => void;
}

export function ArtworkImageInspector(props: ArtworkImageInspectorProps) {
  const discardEpoch = useDiscardEpoch();
  return (
    <ArtworkImageDraft
      key={discardEpoch}
      {...props}
    />
  );
}

function ArtworkImageDraft({
  id,
  titleId,
  entry,
  artwork,
  selections,
  releases = [],
  disabled,
  onClose,
  onDelete,
  onSaved,
  onSaving,
}: ArtworkImageInspectorProps) {
  const cache = useQueryClient();
  const guard = useDraftGuard();
  const classify = useClassifyTitleMedia();
  const [drafts, setDrafts] = useState<Drafts>({});
  const [classification, setClassification] = useState<TitleMediaClassification>();
  const [reading, setReading] = useState(false);
  const [requestedFocalRole, setRequestedFocalRole] = useState<ArtworkRole>();
  const [livePoint, setLivePoint] = useState<FocalPoint>();
  const [saving, setSaving] = useState(false);
  const saveInFlight = useRef(false);
  const [error, setError] = useState<string>();
  const { media } = entry;
  const document = isDocumentType(entry.name);
  // Packaging scans and manuals are collected, not presented; the Presentation tab chooses what players see.
  const presentable = !document && !isPackagingType(entry.name);
  const roles = !presentable
    ? []
    : entry.asset
      ? artworkRoles.filter((role) => role === entry.asset?.role)
      : artworkRoles;
  const savedClassification: TitleMediaClassification | undefined = media && {
    type: media.type,
    catalogReleaseId: media.catalogReleaseId ?? null,
    region: media.region ?? null,
    language: media.language ?? null,
    isPreferred: media.isPreferred ?? false,
  };
  const proposed = classification ?? savedClassification;
  const classificationDirty =
    !!classification &&
    !!savedClassification &&
    JSON.stringify(normalizeClassification(classification)) !==
      JSON.stringify(normalizeClassification(savedClassification));
  const dirty = Object.keys(drafts).length > 0 || classificationDirty;
  useDraft(dirty, 'Your artwork changes have not been saved.');
  // Uploads and gallery imports are the curator's own classification; enrichment media keeps the provider's type.
  const curated = media?.sourceId === 'user' || !!media?.sourceId.startsWith('gallery:');
  const changeClassification = (values: Partial<TitleMediaClassification>) => {
    if (!proposed || locked) return;
    setError(undefined);
    const next = {
      ...proposed,
      ...values,
    };
    if (!isPackagingType(next.type)) next.isPreferred = false;
    setClassification(next);
  };

  const savedPoint = (role: ArtworkRole): FocalPoint => {
    const current = entry.roles.includes(role)
      ? artwork.find((item) => item.role === role)
      : undefined;
    return {
      focalX: current?.focalX ?? 50,
      focalY: current?.focalY ?? 50,
    };
  };
  const focalRoles = roles.filter(
    (role) =>
      (role === 'Backdrop' || role === 'Hero') &&
      (drafts[role] ? drafts[role]?.mode === 'Pinned' : entry.roles.includes(role)),
  );
  const focalRole =
    requestedFocalRole && focalRoles.includes(requestedFocalRole)
      ? requestedFocalRole
      : focalRoles[0];
  const focalPoint = focalRole && (livePoint ?? drafts[focalRole] ?? savedPoint(focalRole));
  const locked = disabled || saving;

  const stage = (role: ArtworkRole, mode: ArtworkMode | '', point?: FocalPoint) => {
    const selection = selections?.find((item) => item.role === role);
    if (!selection || locked) return;
    setError(undefined);
    setLivePoint(undefined);
    setDrafts((current) => {
      const next = {
        ...current,
      };
      if (!mode) {
        delete next[role];
      } else {
        next[role] = {
          ...savedPoint(role),
          ...current[role],
          ...point,
          mode,
          revision: current[role]?.revision ?? selection.revision,
        };
      }
      return next;
    });
  };

  const save = async () => {
    if (!dirty || locked || saveInFlight.current) return;
    saveInFlight.current = true;
    setSaving(true);
    setError(undefined);
    onSaving(true);
    let accepted = 0;
    let failedRole: ArtworkRole | undefined;
    let message: string | undefined;
    let refreshNeeded = false;
    if (classificationDirty && classification && media) {
      try {
        await classify.mutateAsync({
          titleId,
          mediaId: media.id,
          classification: normalizeClassification(classification),
        });
        setClassification(undefined);
      } catch (reason) {
        setError(
          reason instanceof TitleConflictError
            ? 'The title changed while you were editing. It was reloaded; review the classification and save again.'
            : reason instanceof Error
              ? reason.message
              : 'Could not save the classification.',
        );
        if (reason instanceof TitleConflictError) setClassification(undefined);
        saveInFlight.current = false;
        setSaving(false);
        onSaving(false);
        return;
      }
    }
    try {
      // The API versions roles independently. Record each accepted role so retry only sends untouched changes.
      for (const role of roles) {
        const draft = drafts[role];
        if (!draft) continue;
        failedRole = role;
        const target = artworkTargetForRole(entry, role);
        const response = await replaceTitleArtworkRole({
          path: {
            titleId,
            role,
          },
          headers: ifMatch(draft.revision),
          body:
            draft.mode === 'Pinned'
              ? {
                  mode: 'Pinned',
                  assetId: target.assetId ?? null,
                  mediaId: target.mediaId ?? null,
                  focalX: draft.focalX,
                  focalY: draft.focalY,
                }
              : {
                  mode: draft.mode,
                },
        });
        if (response.error || !response.data) {
          if (response.response?.status === 412 || response.response?.status === 409) {
            refreshNeeded = true;
            message =
              'This role changed while you were editing. Discard its change and review the latest selection.';
          } else {
            message = response.error?.detail ?? 'Could not save this role. Try again.';
          }
          break;
        }
        accepted += 1;
        await storeArtwork(cache, titleId, response.data);
        setDrafts((current) => {
          const next = {
            ...current,
          };
          delete next[role];
          return next;
        });
      }
    } catch {
      message = 'Could not finish saving artwork. Review the selection and try again.';
      refreshNeeded = true;
    } finally {
      if (accepted || refreshNeeded) await invalidateTitle(cache, titleId);
      saveInFlight.current = false;
      setSaving(false);
      onSaving(false);
    }

    if (message) {
      const applied = accepted
        ? `${accepted} ${accepted === 1 ? 'role was' : 'roles were'} saved. `
        : '';
      setError(`${applied}${failedRole ? `${artworkRoleLabel(failedRole)}: ` : ''}${message}`);
    } else {
      onSaved();
    }
  };

  const releaseName = (id?: string | null) =>
    id ? (releases.find((item) => item.value === id)?.label ?? 'this release') : 'the title';
  const size = entry.asset?.artwork ?? entry.retained?.[0]?.artwork;
  const dimensions =
    size?.originalWidth && size.originalHeight
      ? `${size.originalWidth} × ${size.originalHeight}`
      : size?.width && size.height
        ? `${size.width} × ${size.height}`
        : undefined;
  const detail = [
    artworkSourceLabel(entry.source),
    media && placementLabel(media, releases),
    dimensions,
    entry.media?.file ? formatBytes(entry.media.file.sizeOnDiskBytes) : undefined,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <InspectorPanel
      id={id}
      title={typeLabel(entry.name)}
      onClose={onClose}
      footer={
        <EditorFooter
          dirty={dirty}
          standalone={false}
          saving={saving}
          disabled={disabled}
          onDiscard={() => {
            setDrafts({});
            setClassification(undefined);
            setError(undefined);
            setLivePoint(undefined);
          }}
          onDelete={() => void guard.discardThen(onDelete)}
          deleteLabel={document ? 'Delete manual' : 'Delete image'}
          onSave={() => void save()}
        />
      }
    >
      <Stack gap="md">
        {document && media ? (
          <Stack
            gap="xs"
            align="center"
          >
            <ManualCover
              titleId={titleId}
              mediaId={media.id}
              width={180}
              label={`Cover of ${typeLabel(entry.name).toLowerCase()}`}
            />
            <Button
              {...workspaceActionProps}
              leftSection={<IconBook size={16} />}
              onClick={() => setReading(true)}
            >
              Open manual
            </Button>
            <ManualViewer
              titleId={titleId}
              mediaId={media.id}
              title={`Manual · ${placementLabel(media, releases)}`}
              fileName={`manual-${media.id}.pdf`}
              opened={reading}
              onClose={() => setReading(false)}
            />
          </Stack>
        ) : (
          <ArtworkSurface background={entry.name === 'Logo' ? 'checkerboard' : 'dark'}>
            <Image
              src={entry.url || undefined}
              alt={typeLabel(entry.name)}
              className={classes.imagePreview}
              fit="contain"
            />
          </ArtworkSurface>
        )}
        <Group
          justify="space-between"
          gap="xs"
        >
          <Text
            size="sm"
            c="dimmed"
          >
            {detail}
          </Text>
          {entry.url && !document && (
            <Button
              {...workspaceActionProps}
              component="a"
              href={entry.url}
              download
              variant="light"
              size="compact-sm"
              leftSection={<IconDownload size={16} />}
            >
              Download
            </Button>
          )}
        </Group>
        {media && (
          <dl className={classes.facts}>
            <dt>Source</dt>
            <dd>
              {artworkSourceLabel(entry.source)}
              {media.providerAssetId ? ` · ${media.providerAssetId}` : ''}
            </dd>
            {media.attribution && (
              <>
                <dt>Attribution</dt>
                <dd>{media.attribution}</dd>
              </>
            )}
            {media.sourcePageUrl && (
              <>
                <dt>Original</dt>
                <dd>
                  <Anchor
                    size="sm"
                    href={media.sourcePageUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    View original
                  </Anchor>
                </dd>
              </>
            )}
            <dt>Region</dt>
            <dd>{media.region ?? 'Not recorded'}</dd>
            <dt>Language</dt>
            <dd>{media.language ?? 'Not recorded'}</dd>
          </dl>
        )}
        {proposed && (
          <Stack gap="xs">
            <Title
              order={4}
              className={classes.heading}
            >
              Classification
            </Title>
            <NativeSelect
              label="Type"
              value={proposed.type}
              disabled={locked || !curated}
              description={
                curated ? undefined : 'Media from metadata enrichment keeps its provider type.'
              }
              data={compatibleMediaTypes(proposed.type).map((value) => ({
                value,
                label: mediaTypeLabel(value),
              }))}
              onChange={(event) =>
                changeClassification({
                  type: event.currentTarget.value,
                })
              }
            />
            <NativeSelect
              label="Release"
              value={proposed.catalogReleaseId ?? ''}
              disabled={locked}
              data={[
                {
                  value: '',
                  label: 'Title-level (any release)',
                },
                ...releases.map((release) => ({
                  value: release.value,
                  label: release.label,
                })),
              ]}
              onChange={(event) => {
                const releaseId = event.currentTarget.value || null;
                const release = releases.find((item) => item.value === releaseId);
                changeClassification({
                  catalogReleaseId: releaseId,
                  // A release names its region; fill it when the item has none yet.
                  region: proposed.region || release?.region || null,
                });
              }}
            />
            <Group
              grow
              gap="xs"
            >
              <TextInput
                label="Region"
                maxLength={100}
                value={proposed.region ?? ''}
                disabled={locked}
                onChange={(event) =>
                  changeClassification({
                    region: event.currentTarget.value,
                  })
                }
              />
              <TextInput
                label="Language"
                maxLength={100}
                value={proposed.language ?? ''}
                disabled={locked}
                onChange={(event) =>
                  changeClassification({
                    language: event.currentTarget.value,
                  })
                }
              />
            </Group>
            {isPackagingType(proposed.type) && (
              <Switch
                label={`Preferred ${mediaTypeLabel(proposed.type).toLowerCase()} for ${releaseName(proposed.catalogReleaseId)}`}
                description="Alternatives stay in the library. Choosing this one replaces the current preference."
                checked={proposed.isPreferred ?? false}
                disabled={locked}
                onChange={(event) =>
                  changeClassification({
                    isPreferred: event.currentTarget.checked,
                  })
                }
              />
            )}
          </Stack>
        )}
        {roles.length > 0 && (
          <Stack gap="xs">
            <Title
              order={4}
              className={classes.heading}
            >
              Role assignments
            </Title>
            {roles.map((role) => {
              const selection = selections?.find((item) => item.role === role);
              const assigned = entry.roles.includes(role);
              const roleLabel = artworkRoleLabel(role);
              const current = selection?.pendingJobId
                ? 'Artwork is processing'
                : assigned
                  ? selection?.mode === 'Pinned'
                    ? 'Selected by you'
                    : 'Used automatically'
                  : artwork.some(
                        (item) =>
                          item.role === role && item.url && item.fallbackReason !== 'LegacyCover',
                      )
                    ? 'Uses another image'
                    : selection?.mode === 'Empty'
                      ? 'Left empty'
                      : 'No image selected';
              return (
                <NativeSelect
                  key={role}
                  label={roleLabel}
                  aria-label={`${roleLabel} assignment`}
                  value={drafts[role]?.mode ?? ''}
                  description={
                    drafts[role]?.mode === 'Empty'
                      ? 'Automatic artwork runs will leave this role empty.'
                      : undefined
                  }
                  disabled={!entry.url || !selection || locked}
                  onChange={(event) => {
                    const { value } = event.currentTarget;
                    if (
                      value === '' ||
                      value === 'Pinned' ||
                      value === 'Empty' ||
                      value === 'Automatic'
                    )
                      stage(role, value);
                  }}
                  data={[
                    {
                      value: '',
                      label: current,
                    },
                    ...(assigned && selection?.mode === 'Pinned'
                      ? []
                      : [
                          {
                            value: 'Pinned',
                            label: assigned ? 'Keep this image' : 'Use this image',
                          },
                        ]),
                    ...(assigned
                      ? [
                          {
                            value: 'Automatic',
                            label: 'Return to automatic',
                          },
                          {
                            value: 'Empty',
                            label: 'Leave empty',
                          },
                        ]
                      : []),
                    ...(assigned && selection?.mode === 'Pinned' && drafts[role]?.mode === 'Pinned'
                      ? [
                          {
                            value: 'Pinned',
                            label: 'Keep this image and adjust focal point',
                          },
                        ]
                      : []),
                  ]}
                />
              );
            })}
          </Stack>
        )}
        {focalRole && focalPoint && entry.url && (
          <Stack
            gap="xs"
            align="flex-start"
          >
            <Title
              order={4}
              className={classes.heading}
            >
              Focal point
            </Title>
            {focalRoles.length > 1 && (
              <Select
                aria-label="Focal point role"
                value={focalRole}
                allowDeselect={false}
                onChange={(value) => {
                  if (value && isArtworkRole(value)) {
                    setRequestedFocalRole(value);
                    setLivePoint(undefined);
                  }
                }}
                data={focalRoles.map((role) => ({
                  value: role,
                  label: artworkRoleLabel(role),
                }))}
              />
            )}
            <FocalPointEditor
              key={focalRole}
              src={entry.url}
              value={focalPoint}
              maxHeight={180}
              disabled={locked || !selections?.some((item) => item.role === focalRole)}
              onChange={setLivePoint}
              onCommit={(point) => stage(focalRole, 'Pinned', point)}
            />
            <Text
              size="xs"
              c="dimmed"
            >
              Drag or use the arrow keys to position the crop.
            </Text>
          </Stack>
        )}
        {error && (
          <Alert
            color="red"
            role="alert"
          >
            {error}
          </Alert>
        )}
      </Stack>
    </InspectorPanel>
  );
}

/** Trims text and sends blank fields as absent, as the server stores them. */
function normalizeClassification(value: TitleMediaClassification): TitleMediaClassification {
  return {
    type: value.type,
    catalogReleaseId: value.catalogReleaseId || null,
    region: value.region?.trim() || null,
    language: value.language?.trim() || null,
    isPreferred: value.isPreferred ?? false,
  };
}
