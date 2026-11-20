import {
  Alert,
  Badge,
  Button,
  Group,
  Image,
  Loader,
  Modal,
  NativeSelect,
  Stack,
  Tabs,
  Text,
  Title,
  UnstyledButton,
} from '@mantine/core';
import type { TitleProviderMatch } from '@romd/admin-api-client';
import { deleteTitleArtworkAsset } from '@romd/admin-api-client';
import { IconPhoto, IconPlus, IconUpload } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { useLocation, useSearchParams } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch, TitleConflictError } from '../../hooks/api/titleConflict';
import {
  artworkKeys,
  storeArtwork,
  useArtworkSelections,
  useSavedArtwork,
} from '../../hooks/api/useArtwork';
import { useDeleteTitleMedia } from '../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { useDraftGuard } from '../Administration/DraftGuard';
import { ArtworkSurface } from '../ArtworkSurface';
import {
  artworkRoleLabel,
  artworkRoles,
  artworkSourceLabel,
  type ArtworkRole as Role,
} from '../artworkPresentation';
import { isDocumentType } from '../mediaTypes';
import { useInspectorSelection } from '../Workspace/useInspectorSelection';
import workspaceClasses from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { AddArtwork, type AddMediaMode } from './AddArtwork';
import { ArtworkImageInspector } from './ArtworkImageInspector';
import classes from './ArtworkWorkspace.module.css';
import { artworkLibraryEntries, type SavedArtworkTarget } from './artworkLibrary';
import { typeLabel } from './BatchArtworkUpload';
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
import { ProviderMatchPicker } from './ProviderMatches';
import { PresentationStudio } from './presentation/PresentationStudio';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

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
  const panelId = `${statusId}-artwork-item`;
  const gallery = useRef<HTMLFieldSetElement>(null);
  const previousItem = useRef<string>(undefined);
  const guard = useDraftGuard();
  const artwork = useArtworkSelections(titleId);
  const saved = useSavedArtwork(titleId);
  const removeMedia = useDeleteTitleMedia();
  const [deleting, setDeleting] = useState<{
    target: SavedArtworkTarget;
    roles: Role[];
  } | null>(null);
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const libraryRequested = params.get('artwork') === 'library';
  const [typeFilter, setTypeFilter] = useState<LibraryTypeFilter>('all');
  const [releaseFilter, setReleaseFilter] = useState<LibraryReleaseFilter>('all');
  const [adding, setAdding] = useState<AddMediaMode | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editingMatch, setEditingMatch] = useState<TitleProviderMatch | null>(null);
  const [studioBusy, setStudioBusy] = useState(false);
  const [deletingBusy, setDeletingBusy] = useState(false);
  const [imageSaving, setImageSaving] = useState(false);
  const entries = artworkLibraryEntries(title.media ?? [], saved.data ?? [], title.artwork ?? []);
  const releases = releaseOptions(title.releases ?? []);
  const cards = entries.filter((entry) => matchesLibraryFilter(entry, typeFilter, releaseFilter));
  const selectedKey = params.get('image');
  const libraryView = libraryRequested || entries.some((entry) => entry.key === selectedKey);
  // The library's lightbox: an item stays open when a filter hides it, and steps through what is shown.
  const selection = useInspectorSelection({
    param: 'image',
    ids: libraryView ? entries.map((entry) => entry.key) : [],
    panelId,
    canChange: () => !deletingBusy && !imageSaving,
    beforeChange: (action) => void guard.discardThen(action),
  });
  const selectedEntry = entries.find((entry) => entry.key === selection.selectedId);
  const openItem = selectedEntry?.key;
  useEffect(() => {
    const closedItem = previousItem.current;
    previousItem.current = openItem;
    if (openItem || !closedItem || !libraryView) return;
    // Restore focus after the lightbox and any discard confirmation have unmounted.
    const frame = requestAnimationFrame(() => {
      const row = Array.from(
        gallery.current?.querySelectorAll<HTMLElement>('[data-inspector-row]') ?? [],
      ).find((element) => element.dataset.inspectorRow === closedItem);
      row?.querySelector<HTMLElement>('[data-inspector-trigger]')?.focus({
        preventScroll: true,
      });
    });
    return () => cancelAnimationFrame(frame);
  }, [
    openItem,
    libraryView,
  ]);
  const position = selectedEntry ? cards.indexOf(selectedEntry) : -1;
  const locked = studioBusy || deletingBusy || imageSaving;
  const refresh = () => invalidateTitle(cache, titleId);

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
    setDeletingBusy(true);
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
      if (selectedEntry && selectedEntry.target === target) selection.close();
      setDeleting(null);
      setNotice('Image deleted from this title.');
    } catch (reason) {
      setError(
        reason instanceof TitleConflictError
          ? 'The title changed since you opened it. It was reloaded; delete the image again.'
          : 'Could not delete image. Try again.',
      );
    } finally {
      setDeletingBusy(false);
    }
  };

  /** Switches between Presentation and the Image library in one history entry, closing any open item. */
  const changeView = (next: string | null) => {
    if ((next === 'library') === libraryView || locked) return;
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
  const filledRoles =
    title.artwork
      ?.filter((item) => item.url && item.fallbackReason !== 'LegacyCover')
      .map((item) => item.role) ?? [];
  const filtered = typeFilter !== 'all' || releaseFilter !== 'all';
  const neighbour = (direction: -1 | 1) => {
    const next = position >= 0 ? cards[position + direction] : undefined;
    return next ? () => selection.select(next.key) : undefined;
  };

  return (
    <Stack
      className={classes.workspace}
      data-density={density}
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
      {notice && (
        <Text
          size="sm"
          role="status"
        >
          {notice}
        </Text>
      )}
      {error && !deleting && (
        <Alert
          color="red"
          role="alert"
        >
          {error}
        </Alert>
      )}
      {!libraryView && (
        <PresentationStudio
          titleId={titleId}
          title={title}
          initialRole={initialRole}
          onResolved={onResolved}
          onDismissed={onDismissed}
          onDelete={requestDelete}
          onEditMatch={setEditingMatch}
          onBusyChange={setStudioBusy}
        />
      )}
      {libraryView && (
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
              ref={gallery}
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
                      aria-haspopup="dialog"
                      aria-label={`Preview ${typeLabel(entry.name).toLowerCase()} from ${artworkSourceLabel(entry.source)}${entry.media?.catalogReleaseId || entry.media?.region ? `, ${placement}` : ''}`}
                      disabled={locked}
                      onClick={(event) => selection.select(entry.key, event.currentTarget)}
                    >
                      <ArtworkSurface background={entry.name === 'Logo' ? 'checkerboard' : 'dark'}>
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
      )}
      {selectedEntry && (
        <ArtworkImageInspector
          key={selectedEntry.key}
          id={panelId}
          titleId={titleId}
          title={title}
          entry={selectedEntry}
          artwork={title.artwork ?? []}
          selections={artwork.data?.roles}
          releases={releases}
          disabled={deletingBusy}
          position={
            position >= 0
              ? {
                  index: position,
                  total: cards.length,
                }
              : undefined
          }
          onPrevious={neighbour(-1)}
          onNext={neighbour(1)}
          onClose={selection.close}
          onDelete={() => requestDelete(selectedEntry.target, selectedEntry.roles)}
          onSaving={setImageSaving}
          onSaved={() => {
            setNotice('Changes saved.');
            onResolved?.();
          }}
        />
      )}
      <Modal
        opened={deleting !== null}
        onClose={() => {
          if (!deletingBusy) {
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
              disabled={deletingBusy}
              onClick={() => {
                setDeleting(null);
                setError(null);
              }}
            >
              Cancel
            </Button>
            <Button
              color="red"
              loading={deletingBusy}
              onClick={() => void deleteImage()}
            >
              Delete image
            </Button>
          </Group>
        </Stack>
      </Modal>
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
