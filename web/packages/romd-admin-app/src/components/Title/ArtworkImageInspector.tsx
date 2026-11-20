import {
  Alert,
  Button,
  Group,
  Image,
  NativeSelect,
  Select,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import type { ArtworkMode, ArtworkSelectionState, ResolvedArtwork } from '@romd/admin-api-client';
import { replaceTitleArtworkRole } from '@romd/admin-api-client';
import { IconDownload } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch } from '../../hooks/api/titleConflict';
import { storeArtwork } from '../../hooks/api/useArtwork';
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
import { EditorFooter } from '../Workspace/EditorFooter';
import { InspectorPanel } from '../Workspace/InspectorPanel';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import classes from './ArtworkWorkspace.module.css';
import { type ArtworkLibraryEntry, artworkTargetForRole } from './artworkLibrary';
import { typeLabel } from './BatchArtworkUpload';
import { type FocalPoint, FocalPointEditor } from './FocalPointEditor';

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
  disabled,
  onClose,
  onDelete,
  onSaved,
  onSaving,
}: ArtworkImageInspectorProps) {
  const cache = useQueryClient();
  const guard = useDraftGuard();
  const [drafts, setDrafts] = useState<Drafts>({});
  const [requestedFocalRole, setRequestedFocalRole] = useState<ArtworkRole>();
  const [livePoint, setLivePoint] = useState<FocalPoint>();
  const [saving, setSaving] = useState(false);
  const saveInFlight = useRef(false);
  const [error, setError] = useState<string>();
  const roles = entry.asset
    ? artworkRoles.filter((role) => role === entry.asset?.role)
    : artworkRoles;
  const dirty = Object.keys(drafts).length > 0;
  useDraft(dirty, 'Your artwork changes have not been saved.');

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

  const size = entry.asset?.artwork ?? entry.retained?.[0]?.artwork;
  const dimensions =
    size?.originalWidth && size.originalHeight
      ? `${size.originalWidth} × ${size.originalHeight}`
      : size?.width && size.height
        ? `${size.width} × ${size.height}`
        : undefined;
  const detail = [
    artworkSourceLabel(entry.source),
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
            setError(undefined);
            setLivePoint(undefined);
          }}
          onDelete={() => void guard.discardThen(onDelete)}
          deleteLabel="Delete image"
          onSave={() => void save()}
        />
      }
    >
      <Stack gap="md">
        <ArtworkSurface background={entry.name === 'Logo' ? 'checkerboard' : 'dark'}>
          <Image
            src={entry.url || undefined}
            alt={typeLabel(entry.name)}
            className={classes.imagePreview}
            fit="contain"
          />
        </ArtworkSurface>
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
          {entry.url && (
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
