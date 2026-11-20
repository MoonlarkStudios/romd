import {
  ActionIcon,
  Alert,
  Box,
  Button,
  Group,
  SegmentedControl,
  Slider,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import type { ArtworkCandidate } from '@romd/admin-api-client';
import { IconRestore } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { artworkRoleLabel } from '../artworkPresentation';
import { EditorFooter } from '../Workspace/EditorFooter';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import classes from './ArtworkWorkspace.module.css';
import { type FocalPoint, FocalPointEditor } from './FocalPointEditor';
import type { ArtworkRole, SavedArtworkTarget } from './MediaGallery';
import { ArtworkRolePreview } from './preview/ArtworkRolePreview';
import { PreviewDeviceSwitcher, usePreviewDevice } from './preview/PreviewDeviceSwitcher';

export interface ArtworkDraft {
  role: ArtworkRole;
  target?: SavedArtworkTarget;
  candidate?: ArtworkCandidate;
  file?: File;
  url: string;
  revision: string;
  focalX: number;
  focalY: number;
}

/**
 * Persistent composition; the host owns draft and publication state. A focal drag or key press previews live and
 * updates the draft once when it settles; Apply stays the only save. `onReloadLatest` is offered after a revision conflict.
 */
export function ArtworkComposer({
  title,
  role,
  draft,
  busy,
  error,
  onChange,
  onCancel,
  onSave,
  onReloadLatest,
  onSaveToLibrary,
  savedToLibrary = false,
}: {
  title: TitleDetail;
  role: ArtworkRole;
  draft: ArtworkDraft | null;
  busy: boolean;
  error: string | null;
  onChange: (draft: ArtworkDraft) => void;
  onCancel: () => void;
  onSave: (draft: ArtworkDraft) => void;
  onReloadLatest?: () => void;
  /** Offered for a provider candidate: keeps it in the image library without changing the role. */
  onSaveToLibrary?: (draft: ArtworkDraft) => void;
  savedToLibrary?: boolean;
}) {
  const [settling, setSettling] = useState<FocalPoint | null>(null);
  const focalX = settling?.focalX ?? draft?.focalX ?? 50;
  const focalY = settling?.focalY ?? draft?.focalY ?? 50;
  const { ref: previewColumn, device, choose } = usePreviewDevice();
  const [comparison, setComparison] = useState('preview');
  const [posterContext, setPosterContext] = useState('library');
  const previewRef = useRef<HTMLElement>(null);
  const draftIdentity =
    draft?.candidate?.reference ?? draft?.target?.assetId ?? draft?.target?.mediaId ?? draft?.url;
  useEffect(() => {
    setComparison('preview');
    const composition = previewRef.current;
    // Candidate browsing can put the preview above the visible part of the inspector.
    if (!draftIdentity || !composition) return;
    composition.focus({
      preventScroll: true,
    });
    composition.scrollIntoView?.({
      block: 'nearest',
    });
  }, [
    draftIdentity,
  ]);
  const previewTitle: TitleDetail =
    !draft || comparison === 'current'
      ? title
      : {
          ...title,
          artwork: [
            ...(title.artwork ?? []).filter((item) => item.role !== role),
            {
              role,
              assetId: null,
              contentVersion: null,
              width: null,
              height: null,
              originalWidth: null,
              originalHeight: null,
              url: draft.url,
              focalX,
              focalY,
              fit: role === 'Logo' ? 'Contain' : 'Cover',
              fallbackReason: 'None',
              variants: [],
            },
          ],
        };
  const changeFocal = (x: number, y: number) => {
    if (draft)
      onChange({
        ...draft,
        focalX: x,
        focalY: y,
      });
    setComparison('preview');
  };
  return (
    <section
      ref={previewRef}
      tabIndex={-1}
      aria-label={`Compose ${artworkRoleLabel(role).toLowerCase()}`}
      className={classes.composition}
    >
      <div className={classes.editor}>
        <div className={classes.editorBody}>
          <Stack gap="md">
            <Group
              justify="space-between"
              className={classes.composerCompare}
            >
              {draft ? (
                <SegmentedControl
                  aria-label="Artwork comparison"
                  value={comparison}
                  onChange={setComparison}
                  data={[
                    {
                      value: 'current',
                      label: 'Current',
                    },
                    {
                      value: 'preview',
                      label: 'Proposed',
                    },
                  ]}
                />
              ) : (
                <Text
                  size="sm"
                  fw={600}
                >
                  Current presentation
                </Text>
              )}
              <PreviewDeviceSwitcher
                value={device}
                onChange={choose}
              />
            </Group>
            {role === 'Backdrop' && draft?.candidate && (
              <Text
                size="sm"
                c="dimmed"
              >
                {draft?.candidate.width} × {draft?.candidate.height}
                {draft?.candidate.width < 1920 || draft?.candidate.height < 1080
                  ? ' · Below full HD; may look soft on a large display.'
                  : ''}
              </Text>
            )}
            {role === 'Poster' && (
              <SegmentedControl
                aria-label="Poster context"
                value={posterContext}
                onChange={setPosterContext}
                data={[
                  {
                    value: 'library',
                    label: 'Library card',
                  },
                  {
                    value: 'details',
                    label: 'Game details',
                  },
                ]}
              />
            )}
            <Box ref={previewColumn}>
              <ArtworkRolePreview
                title={previewTitle}
                artworkRole={role}
                device={device}
                posterContext={posterContext === 'details' ? 'details' : 'library'}
              />
            </Box>
            {role === 'Logo' && draft?.candidate && (
              <Text
                size="sm"
                c="dimmed"
              >
                {draft.candidate.width} × {draft.candidate.height}
                {draft.candidate.style ? ` · ${draft.candidate.style}` : ''}
                {draft.candidate.attribution ? ` · By ${draft.candidate.attribution}` : ''}
              </Text>
            )}
            {draft && (role === 'Hero' || role === 'Backdrop') && (
              <Group
                align="flex-start"
                gap="lg"
              >
                <Stack
                  gap="xs"
                  style={{
                    flex: '1 1 240px',
                    minWidth: 0,
                  }}
                >
                  <Group justify="space-between">
                    <Text
                      size="sm"
                      fw={600}
                    >
                      Focal point
                    </Text>
                    <Tooltip label="Center focal point">
                      <ActionIcon
                        aria-label="Center focal point"
                        variant="subtle"
                        disabled={busy}
                        onClick={() => {
                          changeFocal(50, 50);
                        }}
                      >
                        <IconRestore size={17} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                  <Box>
                    <Text
                      size="xs"
                      mb={6}
                    >
                      Horizontal
                    </Text>
                    <Slider
                      thumbLabel="Horizontal focal point"
                      value={focalX}
                      onChange={(value) => {
                        changeFocal(value, focalY);
                      }}
                      disabled={busy}
                      label={(value) => `${value}%`}
                    />
                  </Box>
                  <Box mt="xs">
                    <Text
                      size="xs"
                      mb={6}
                    >
                      Vertical
                    </Text>
                    <Slider
                      thumbLabel="Vertical focal point"
                      value={focalY}
                      onChange={(value) => {
                        changeFocal(focalX, value);
                      }}
                      disabled={busy}
                      label={(value) => `${value}%`}
                    />
                  </Box>
                </Stack>
                <Box
                  m="auto"
                  maw="100%"
                >
                  <FocalPointEditor
                    src={draft.url}
                    value={{
                      focalX,
                      focalY,
                    }}
                    disabled={busy}
                    maxHeight={140}
                    onChange={(point) => {
                      setSettling(point);
                      setComparison('preview');
                    }}
                    onCommit={(point) => {
                      setSettling(null);
                      changeFocal(point.focalX, point.focalY);
                    }}
                  />
                </Box>
              </Group>
            )}
          </Stack>
        </div>
        <div
          aria-live="polite"
          className={classes.composerActions}
        >
          {error && (
            <Alert
              color="red"
              mb="sm"
            >
              {error}
              {onReloadLatest && (
                <Button
                  {...workspaceActionProps}
                  variant="light"
                  size="compact-sm"
                  ml="xs"
                  disabled={busy}
                  onClick={onReloadLatest}
                >
                  Reload latest
                </Button>
              )}
            </Alert>
          )}
          <Group
            justify="space-between"
            gap="xs"
            mb="xs"
          >
            <Text size="sm">{draft ? 'Unsaved preview' : 'Showing saved artwork'}</Text>
            {draft?.candidate && onSaveToLibrary && (
              <Button
                {...workspaceActionProps}
                variant="subtle"
                size="compact-sm"
                disabled={busy || savedToLibrary || draft.candidate.isSaved}
                onClick={() => onSaveToLibrary(draft)}
              >
                {savedToLibrary || draft.candidate.isSaved ? 'Saved to library' : 'Save to library'}
              </Button>
            )}
          </Group>
          <EditorFooter
            dirty={draft !== null}
            saving={busy}
            onDiscard={() => {
              onCancel();
              setComparison('preview');
            }}
            onSave={() => {
              if (draft) onSave(draft);
            }}
            saveLabel={`Use this ${artworkRoleLabel(role).toLowerCase()}`}
          />
        </div>
      </div>
    </section>
  );
}
