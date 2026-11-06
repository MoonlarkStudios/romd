import {
  ActionIcon,
  Alert,
  Badge,
  Box,
  Group,
  Menu,
  Modal,
  Stack,
  Text,
  Title,
  Tooltip,
  UnstyledButton,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { type CreateDownloadTicketRequest, createDownloadTicket } from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import {
  IconBookmark,
  IconBookmarkOff,
  IconDownload,
  IconEdit,
  IconPhoto,
} from '@tabler/icons-react';
import { useState } from 'react';
import { useLocation, useSearchParams } from 'react-router';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { useSetTitleTracking } from '../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { archiveFormatLabel } from '../../utils/archiveFormats';
import { ArtworkFrame } from '../ArtworkFrame';
import { TitleActionMenu } from './TitleActionMenu';
import classes from './TitleDetail.module.css';
import { TitleNameEditor } from './TitleNameEditor';
import { TitleStatusChips } from './TitleStatusChips';

export function TitleHero({
  title,
  titleId,
  compact = false,
}: {
  title: TitleDetail;
  titleId: string;
  compact?: boolean;
}) {
  const [preview, setPreview] = useState(false);
  const [editingName, setEditingName] = useState(false);
  const [savingName, setSavingName] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const { canManageTitles } = usePermissions();
  const poster = title.artwork?.find((artwork) => artwork.role === 'Poster');
  const hero = title.artwork?.find((artwork) => artwork.role === 'Hero');
  const cover =
    title.media?.find((media) => media.type?.toLowerCase() === 'cover' && media.isPrimary) ??
    title.media?.find((media) => media.type?.toLowerCase() === 'cover');
  const sources = useTitleSourceReferences(titleId);
  // The assessment is Manager-only.
  const assessment = useEnrichmentAssessment(canManageTitles ? titleId : undefined);
  const { data: platform } = usePlatform(title.systemKey);
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const tracking = useSetTitleTracking();
  const owned = title.releases?.filter((release) => release.isComplete).length ?? 0;
  const archiveFormats = title.archiveFormats ?? [];
  const download = async (format: CreateDownloadTicketRequest['format']) => {
    if (downloading) return;
    setDownloading(true);
    try {
      const response = await createDownloadTicket({
        body: {
          target: 'Title',
          id: titleId,
          format,
        },
      });
      if (response.error || !response.data) throw new Error('Download unavailable');
      startBrowserDownload(response.data.downloadUrl);
    } catch {
      notifications.show({
        color: 'red',
        message: 'Could not prepare the title download. Try again.',
      });
    } finally {
      setDownloading(false);
    }
  };

  return (
    <Stack gap="sm">
      <Box
        className={classes.hero}
        data-testid="title-header"
        data-compact={compact || undefined}
      >
        {hero?.url && (
          <img
            className={classes.backdrop}
            src={hero.url}
            alt=""
            style={{
              objectPosition: `${hero.focalX ?? 50}% ${hero.focalY ?? 50}%`,
            }}
          />
        )}
        <div className={classes.heroShade} />
        <div className={classes.identity}>
          <div className={classes.poster}>
            <ArtworkFrame
              artworkRole="Poster"
              src={poster?.url ?? cover?.url}
              title={title.name}
              fit={poster?.fit === 'Cover' ? 'Cover' : 'Contain'}
            />
          </div>
          <Stack
            gap="sm"
            className={classes.identityText}
          >
            {!compact && platform && (
              <Text
                size="xs"
                fw={600}
              >
                {platform.name}
              </Text>
            )}
            <Group
              gap="xs"
              wrap="nowrap"
              align="flex-start"
            >
              <Title
                order={1}
                className={classes.title}
              >
                {title.name}
              </Title>
              {canManageTitles && (
                <Tooltip label="Edit title name">
                  <ActionIcon
                    aria-label="Edit title name"
                    variant="subtle"
                    color="gray"
                    style={{
                      flexShrink: 0,
                    }}
                    onClick={() => setEditingName(true)}
                  >
                    <IconEdit size={16} />
                  </ActionIcon>
                </Tooltip>
              )}
            </Group>
            {/* Status stays visible on every title page tab; compact headers drop only the descriptive details. */}
            {(!compact || assessment.data) && (
              <Group gap="xs">
                {!compact && title.releaseDate && (
                  <Text size="sm">{title.releaseDate.slice(0, 4)}</Text>
                )}
                {!compact && title.genre && (
                  <Badge
                    variant="light"
                    color="blue"
                  >
                    {title.genre}
                  </Badge>
                )}
                {assessment.data && (
                  <UnstyledButton
                    aria-label="View enrichment details"
                    onClick={() => {
                      const next = new URLSearchParams(params);
                      next.set('editor', 'enrichment');
                      next.delete('requirement');
                      next.delete('role');
                      setParams(next, {
                        state: location.state,
                      });
                    }}
                  >
                    <TitleStatusChips
                      flags={assessment.data}
                      describeSettled
                    />
                  </UnstyledButton>
                )}
              </Group>
            )}
            <Group gap="xs">
              <Tooltip label={title.isTracked ? 'Untrack title' : 'Track title'}>
                <ActionIcon
                  aria-label={title.isTracked ? 'Untrack title' : 'Track title'}
                  color={title.isTracked ? 'yellow' : 'gray'}
                  variant="light"
                  size="lg"
                  loading={tracking.isPending}
                  onClick={() =>
                    tracking.mutate({
                      titleId,
                      tracked: !title.isTracked,
                    })
                  }
                >
                  {title.isTracked ? <IconBookmark size={18} /> : <IconBookmarkOff size={18} />}
                </ActionIcon>
              </Tooltip>
              {owned > 0 &&
                (archiveFormats.length > 1 ? (
                  <Menu position="bottom-end">
                    <Menu.Target>
                      <ActionIcon
                        aria-label="Download title ZIP"
                        loading={downloading}
                        variant="light"
                        color="green"
                        size="lg"
                      >
                        <IconDownload size={18} />
                      </ActionIcon>
                    </Menu.Target>
                    <Menu.Dropdown>
                      {archiveFormats.map((choice) => (
                        <Menu.Item
                          key={choice.format}
                          onClick={() => void download(choice.format)}
                        >
                          {archiveFormatLabel(choice)}
                        </Menu.Item>
                      ))}
                    </Menu.Dropdown>
                  </Menu>
                ) : (
                  <Tooltip label="Download title ZIP">
                    <ActionIcon
                      aria-label="Download title ZIP"
                      loading={downloading}
                      variant="light"
                      color="green"
                      size="lg"
                      onClick={() => void download(archiveFormats[0]?.format)}
                    >
                      <IconDownload size={18} />
                    </ActionIcon>
                  </Tooltip>
                ))}
              <TitleActionMenu
                title={title}
                titleId={titleId}
              />
            </Group>
          </Stack>
        </div>
        {hero?.url && (
          <Tooltip label="Preview hero artwork">
            <UnstyledButton
              className={classes.preview}
              aria-label="Preview hero artwork"
              onClick={() => setPreview(true)}
            >
              <IconPhoto size={18} />
            </UnstyledButton>
          </Tooltip>
        )}
      </Box>
      {sources.data && !sources.data.some((source) => source.hasActiveDefinition) && (
        <Alert
          color="gray"
          title="No active catalog definition"
        >
          Your title and personal state remain saved. Add or re-enable a catalog source to restore
          definitions.
        </Alert>
      )}
      <Modal
        opened={preview}
        onClose={() => setPreview(false)}
        title={`${title.name} artwork`}
        size="xl"
      >
        <img
          src={hero?.url ?? undefined}
          alt={`${title.name} hero`}
          style={{
            display: 'block',
            width: '100%',
            maxHeight: '75vh',
            objectFit: 'contain',
          }}
        />
      </Modal>
      <Modal
        opened={editingName}
        onClose={() => !savingName && setEditingName(false)}
        title="Edit title name"
        closeButtonProps={{
          disabled: savingName,
        }}
      >
        <TitleNameEditor
          key={`${titleId}-${editingName}`}
          titleId={titleId}
          name={title.name}
          onClose={() => setEditingName(false)}
          onSavingChange={setSavingName}
        />
      </Modal>
    </Stack>
  );
}
