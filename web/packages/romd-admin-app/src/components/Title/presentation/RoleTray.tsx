import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  FileInput,
  Group,
  Loader,
  Menu,
  Progress,
  SegmentedControl,
  SimpleGrid,
  Stack,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import type {
  ArtworkCandidate,
  ArtworkProvider,
  ArtworkSelectionState,
  ResolvedArtwork,
  SavedArtwork,
  TitleProviderMatch,
} from '@romd/admin-api-client';
import { IconDotsVertical, IconRestore, IconX } from '@tabler/icons-react';
import { useState } from 'react';
import type { TitleDetailView as TitleDetail } from '../../../hooks/api/useTitleDetail';
import { ArtworkFrame } from '../../ArtworkFrame';
import { type ArtworkRole, artworkRoleLabel } from '../../artworkPresentation';
import { workspaceActionProps } from '../../Workspace/workspaceActions';
import classes from '../ArtworkWorkspace.module.css';
import type { SavedArtworkTarget } from '../artworkLibrary';
import { CandidateBrowser } from '../CandidateBrowser';
import { CropPreviews, type FocalPoint, FocalPointEditor } from '../FocalPointEditor';
import { MediaGallery } from '../MediaGallery';
import type { ArtworkPreviewTitle } from '../preview/ArtworkRolePreview';
import { CropFrames, type RoleStatus } from './PresentationCanvas';
import {
  focalRoles,
  roleStateColor,
  roleStateLabel,
  type StagedChange,
  stagedLabel,
} from './presentationModel';

export interface RoleJobStatus {
  color: string;
  title: string;
  message: string;
  jobId: string;
}

/**
 * The selected role's image source, selection mode and crop. Choices
 * stage a change the canvas shows at once; the save bar commits every staged role together.
 */
export function RoleTray({
  panelId,
  titleId,
  title,
  proposedTitle,
  role,
  status,
  staged,
  selection,
  currentArtwork,
  currentSource,
  activeFrame,
  onFrameChange,
  source,
  onSourceChange,
  providers,
  providersPending,
  providersError,
  onRetryProviders,
  matches,
  onEditMatch,
  saved,
  savedLoading,
  savedError,
  onRetrySaved,
  busy,
  error,
  importing,
  failed,
  jobStatus,
  onRetryImport,
  onChooseAnother,
  onChooseSaved,
  onChooseCandidate,
  onChooseFile,
  onFocalLive,
  onFocalCommit,
  onAutomatic,
  onEmpty,
  onKeep,
  onUndo,
  onDelete,
  onSaveToLibrary,
  savedToLibrary,
  onClose,
}: {
  panelId: string;
  titleId: string;
  title: TitleDetail;
  /** The title with every staged change, so crop frames measure the image being placed. */
  proposedTitle: ArtworkPreviewTitle;
  role: ArtworkRole;
  status: RoleStatus;
  staged?: StagedChange;
  selection?: ArtworkSelectionState;
  currentArtwork?: ResolvedArtwork;
  currentSource?: string;
  /** The crop frame the canvas is showing, dimmed around in the focal point editor. */
  activeFrame?: string;
  onFrameChange: (label: string) => void;
  source: string;
  onSourceChange: (source: string) => void;
  providers?: ArtworkProvider[];
  providersPending: boolean;
  providersError: boolean;
  onRetryProviders: () => void;
  matches?: TitleProviderMatch[];
  onEditMatch: (match: TitleProviderMatch, source?: string) => void;
  saved?: SavedArtwork[];
  savedLoading: boolean;
  savedError: boolean;
  onRetrySaved: () => void;
  busy: boolean;
  error?: string;
  importing: boolean;
  failed: boolean;
  jobStatus?: RoleJobStatus;
  onRetryImport: () => void;
  onChooseAnother: () => void;
  onChooseSaved: (target: SavedArtworkTarget, url?: string) => Promise<boolean>;
  onChooseCandidate: (candidate: ArtworkCandidate) => Promise<void>;
  onChooseFile: (file: File) => void;
  onFocalLive: (point: FocalPoint | null) => void;
  onFocalCommit: (point: FocalPoint) => void;
  onAutomatic: () => void;
  onEmpty: () => void;
  onKeep: () => void;
  onUndo: () => void;
  onDelete: (target: SavedArtworkTarget, roles: ArtworkRole[]) => void;
  onSaveToLibrary?: () => void;
  savedToLibrary: boolean;
  onClose: () => void;
}) {
  const [livePoint, setLivePoint] = useState<FocalPoint | null>(null);
  const [view, setView] = useState<'image' | 'crop'>('image');
  const { state, result } = status;
  const label = artworkRoleLabel(role);
  const lower = label.toLowerCase();
  const draft = staged?.kind === 'image' ? staged.draft : undefined;
  const available =
    providers?.filter(
      (item) => item.isAvailable && item.roles.some((capability) => capability.role === role),
    ) ?? [];
  const provider = available.find((item) => item.id === source);
  const mode =
    staged?.kind ??
    (selection?.mode === 'Pinned' ? 'image' : selection?.mode === 'Empty' ? 'empty' : 'automatic');
  const canPinCurrent = !!currentArtwork?.assetId && !!currentArtwork.url;
  const focalUrl =
    draft?.url ?? (mode === 'image' && canPinCurrent ? currentArtwork?.url : undefined);
  const canCrop = focalRoles.includes(role);
  const savedPoint: FocalPoint = draft ?? {
    focalX: currentArtwork?.focalX ?? 50,
    focalY: currentArtwork?.focalY ?? 50,
  };
  const point = livePoint ?? savedPoint;
  const changeFocal = (next: FocalPoint) => {
    setLivePoint(null);
    onFocalLive(null);
    onFocalCommit(next);
  };
  return (
    <section
      id={panelId}
      tabIndex={-1}
      aria-label={`${label} editor`}
      className={classes.tray}
    >
      <div className={classes.trayHeader}>
        <Group
          gap="xs"
          wrap="wrap"
        >
          <Title
            order={4}
            className={classes.heading}
          >
            {label}
          </Title>
          <Badge
            size="sm"
            variant="light"
            color={roleStateColor(state)}
          >
            {roleStateLabel(state)}
            {currentSource && (state === 'pinned' || state === 'automatic')
              ? ` · ${currentSource}`
              : ''}
          </Badge>
          {result && state !== 'failed' && (
            <Badge
              size="sm"
              variant="light"
              color={result === 'Saved' ? 'teal' : 'red'}
            >
              {result}
            </Badge>
          )}
          {staged && (
            <Badge
              size="sm"
              variant="filled"
              color="blue"
            >
              Draft · {stagedLabel(staged)}
            </Badge>
          )}
        </Group>
        <Group gap="xs">
          {staged && (
            <Button
              {...workspaceActionProps}
              size="compact-sm"
              variant="default"
              disabled={busy}
              onClick={onUndo}
            >
              Undo {lower} change
            </Button>
          )}
          {currentArtwork?.assetId && (
            <Menu withinPortal>
              <Menu.Target>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  aria-label={`${label} actions`}
                  disabled={!selection || busy}
                >
                  <IconDotsVertical size={18} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                {currentArtwork?.assetId && (
                  <Menu.Item
                    color="red"
                    onClick={() => {
                      if (currentArtwork.assetId)
                        onDelete(
                          {
                            assetId: currentArtwork.assetId,
                          },
                          [
                            role,
                          ],
                        );
                    }}
                  >
                    Delete current image
                  </Menu.Item>
                )}
              </Menu.Dropdown>
            </Menu>
          )}
          <Tooltip label={`Close ${lower} editor`}>
            <ActionIcon
              variant="subtle"
              color="gray"
              aria-label={`Close ${lower} editor`}
              onClick={onClose}
            >
              <IconX size={18} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </div>
      {importing && (
        <Stack gap={4}>
          <Progress
            value={100}
            animated
            striped
            size="sm"
            aria-label={`Importing ${lower}`}
          />
          <Text
            size="xs"
            c="dimmed"
          >
            The new {lower} is importing. Players keep seeing the current one until it is ready.
          </Text>
        </Stack>
      )}
      {failed && (
        <Alert
          color="red"
          p="xs"
          role="alert"
        >
          <Text size="sm">The {lower} import failed. The current image is unchanged.</Text>
          <Group
            gap="xs"
            mt={6}
          >
            <Button
              size="compact-sm"
              variant="light"
              color="red"
              disabled={busy}
              onClick={onRetryImport}
            >
              Retry
            </Button>
            <Button
              size="compact-sm"
              variant="subtle"
              disabled={busy}
              onClick={() => {
                setView('image');
                onChooseAnother();
              }}
            >
              Choose another
            </Button>
          </Group>
        </Alert>
      )}
      {jobStatus && !failed && (
        <Alert
          color={jobStatus.color}
          title={jobStatus.title}
          p="xs"
        >
          {jobStatus.message}
          <Anchor
            href={`/jobs/${jobStatus.jobId}`}
            ml="xs"
          >
            View job
          </Anchor>
        </Alert>
      )}
      {staged?.kind === 'empty' && (
        <Text
          size="sm"
          c="dimmed"
        >
          Players will see no {lower}, and automatic runs will not fill it.
        </Text>
      )}
      {staged?.kind === 'automatic' && (
        <Text
          size="sm"
          c="dimmed"
        >
          The server chooses the {lower} again when you save; the canvas keeps the current image
          until then.
        </Text>
      )}
      {error && (
        <Alert
          color="red"
          role="alert"
          p="xs"
        >
          {error}
        </Alert>
      )}
      <Group
        role="group"
        aria-label={`${label} selection mode`}
        gap="xs"
      >
        <Button
          {...workspaceActionProps}
          size="compact-sm"
          variant={mode === 'automatic' ? 'light' : 'default'}
          aria-pressed={mode === 'automatic'}
          disabled={busy || !selection}
          onClick={() => {
            if (mode !== 'automatic') onAutomatic();
          }}
        >
          Automatic
        </Button>
        <Button
          {...workspaceActionProps}
          size="compact-sm"
          variant={mode === 'image' ? 'light' : 'default'}
          aria-pressed={mode === 'image'}
          disabled={busy || !selection}
          onClick={() => {
            if (mode === 'image') return;
            if (!staged && canPinCurrent) onKeep();
            else if (selection?.mode === 'Pinned') onUndo();
            else setView('image');
          }}
        >
          {mode === 'image' || selection?.mode === 'Pinned'
            ? 'Pinned'
            : !staged && canPinCurrent
              ? 'Pin this image'
              : 'Choose image to pin'}
        </Button>
        <Button
          {...workspaceActionProps}
          size="compact-sm"
          variant={mode === 'empty' ? 'light' : 'default'}
          aria-pressed={mode === 'empty'}
          disabled={busy || !selection}
          onClick={() => {
            if (mode !== 'empty') onEmpty();
          }}
        >
          Intentionally empty
        </Button>
      </Group>
      <SegmentedControl
        aria-label="Role editor view"
        size="xs"
        fullWidth
        value={view}
        onChange={(value) => setView(value === 'crop' ? 'crop' : 'image')}
        data={[
          {
            value: 'image',
            label: 'Replace image',
          },
          {
            value: 'crop',
            label: role === 'Logo' ? 'Preview logo' : 'Adjust crop',
          },
        ]}
      />
      <div hidden={view !== 'image'}>
        <Stack
          gap="sm"
          className={classes.traySources}
        >
          <SegmentedControl
            aria-label="Image source"
            size="xs"
            value={source}
            onChange={onSourceChange}
            disabled={busy}
            data={[
              {
                value: 'saved',
                label: 'Saved',
              },
              ...available.map((item) => ({
                value: item.id,
                label: item.name,
              })),
              {
                value: 'custom',
                label: 'Upload',
              },
            ]}
          />
          {source === 'saved' && (
            <>
              {savedLoading && (
                <Loader
                  size="sm"
                  aria-label="Loading saved artwork"
                />
              )}
              {savedError && (
                <Alert color="red">
                  Could not load saved artwork. <Button onClick={onRetrySaved}>Retry</Button>
                </Alert>
              )}
              <MediaGallery
                key={role}
                titleId={titleId}
                media={title.media ?? []}
                saved={saved ?? []}
                artwork={title.artwork ?? []}
                role={role}
                inlineSelection
                onAssign={(_, target, url) => onChooseSaved(target, url)}
                onDelete={onDelete}
                busy={busy || !selection}
              />
            </>
          )}
          {source === 'custom' && (
            <FileInput
              key={`${role}:${draft?.file ? draft.url : 'empty'}`}
              label={`${label} image`}
              placeholder="Select image"
              accept="image/jpeg,image/png,image/webp"
              disabled={busy || !selection}
              onChange={(file) => {
                if (file) onChooseFile(file);
              }}
            />
          )}
          {providersError && (
            <Alert color="red">
              Could not load providers. <Button onClick={onRetryProviders}>Retry</Button>
            </Alert>
          )}
          {provider?.gameId && (
            <Stack gap="xs">
              <Group
                justify="space-between"
                gap="xs"
              >
                <Text size="xs">
                  Matched to{' '}
                  <Anchor
                    size="xs"
                    href={provider.gameUrl ?? undefined}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    {provider.gameName ?? provider.name}
                  </Anchor>
                </Text>
                {matches?.find((item) => item.providerId === provider.id) && (
                  <Button
                    variant="subtle"
                    size="compact-xs"
                    disabled={busy}
                    onClick={() => {
                      const match = matches?.find((item) => item.providerId === provider.id);
                      if (match) onEditMatch(match);
                    }}
                  >
                    Change match
                  </Button>
                )}
              </Group>
              <CandidateBrowser
                key={`${role}:${provider.id}:${provider.gameId}`}
                titleId={titleId}
                title={title}
                role={role}
                provider={provider}
                gameId={provider.gameId}
                selectedReference={draft?.candidate?.reference}
                disabled={busy}
                allowHide={!draft?.candidate}
                onChoose={onChooseCandidate}
              />
            </Stack>
          )}
          {!providersPending && !providersError && available.length === 0 && (
            <Stack gap="xs">
              <Text
                size="sm"
                c="dimmed"
              >
                No linked provider is available for this role. Choose a saved image, upload one, or
                review a provider match.
              </Text>
              {matches
                ?.filter((item) => item.capabilities.includes('artwork'))
                .map((item) => (
                  <Button
                    key={item.providerId}
                    variant="light"
                    disabled={busy || !item.isAvailable}
                    // Once matched, the tray returns to this provider's candidates.
                    onClick={() => onEditMatch(item, item.providerId)}
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
      </div>
      {view === 'crop' && (
        <Stack gap="sm">
          {canCrop && focalUrl ? (
            <CropFrames
              title={proposedTitle}
              role={role}
            >
              {(frames) => (
                <Stack gap="xs">
                  <Group justify="space-between">
                    <Text
                      size="sm"
                      fw={600}
                    >
                      Crop
                    </Text>
                    <Tooltip label="Center focal point">
                      <ActionIcon
                        aria-label="Center focal point"
                        variant="subtle"
                        disabled={busy}
                        onClick={() =>
                          changeFocal({
                            focalX: 50,
                            focalY: 50,
                          })
                        }
                      >
                        <IconRestore size={17} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                  <FocalPointEditor
                    key={focalUrl}
                    src={focalUrl}
                    value={point}
                    frames={frames}
                    activeFrame={activeFrame}
                    maxHeight={300}
                    disabled={busy || !selection}
                    onChange={(next) => {
                      setLivePoint(next);
                      onFocalLive(next);
                    }}
                    onCommit={changeFocal}
                  />
                  <CropPreviews
                    src={focalUrl}
                    frames={frames}
                    value={point}
                    activeFrame={activeFrame}
                    onFrameChange={onFrameChange}
                  />
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    Click or drag to place the focal point; arrow keys move it (Shift for 10%).
                    {frames.length > 1 && ' Select a crop preview to see its outline.'}
                  </Text>
                </Stack>
              )}
            </CropFrames>
          ) : role === 'Logo' && mode !== 'empty' && (draft?.url ?? currentArtwork?.url) ? (
            <Stack gap="xs">
              <Text
                size="sm"
                fw={600}
              >
                Logo on light and dark backgrounds
              </Text>
              <SimpleGrid
                type="container"
                cols={{
                  base: 1,
                  '24em': 2,
                }}
              >
                {(
                  [
                    'dark',
                    'light',
                  ] as const
                ).map((background) => (
                  <ArtworkFrame
                    key={background}
                    artworkRole="Logo"
                    src={draft?.url ?? currentArtwork?.url}
                    title={title.name}
                    fit="Contain"
                    background={background}
                  />
                ))}
              </SimpleGrid>
            </Stack>
          ) : canCrop && !staged && mode === 'automatic' && canPinCurrent ? (
            <Button
              {...workspaceActionProps}
              variant="light"
              disabled={busy || !selection}
              onClick={onKeep}
            >
              Pin this image to adjust its crop
            </Button>
          ) : (
            <Text
              size="sm"
              c="dimmed"
            >
              {canCrop
                ? 'Choose a pinned image to adjust its crop.'
                : 'Choose an image to preview the logo.'}
            </Text>
          )}
        </Stack>
      )}
      {draft?.candidate && (
        <Stack gap={4}>
          <Text size="xs">
            {draft.candidate.width} × {draft.candidate.height}
            {draft.candidate.style ? ` · ${draft.candidate.style}` : ''}
            {draft.candidate.attribution ? ` · By ${draft.candidate.attribution}` : ''}
            {role === 'Backdrop' && (draft.candidate.width < 1920 || draft.candidate.height < 1080)
              ? ' · Below full HD; may look soft on a large display.'
              : ''}
          </Text>
          <Group gap="xs">
            <Anchor
              size="xs"
              href={draft.candidate.sourcePageUrl}
              target="_blank"
              rel="noreferrer"
            >
              View original
            </Anchor>
            {onSaveToLibrary && (
              <Button
                {...workspaceActionProps}
                variant="subtle"
                size="compact-sm"
                disabled={busy || savedToLibrary || draft.candidate.isSaved}
                onClick={onSaveToLibrary}
              >
                {savedToLibrary || draft.candidate.isSaved ? 'Saved to library' : 'Save to library'}
              </Button>
            )}
          </Group>
        </Stack>
      )}
    </section>
  );
}
