import {
  ActionIcon,
  Alert,
  Box,
  Group,
  Menu,
  Modal,
  Stack,
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
import { Link, useLocation } from 'react-router';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { useSetTitleTracking } from '../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { archiveFormatLabel } from '../../utils/archiveFormats';
import { TitleIdentity } from '../Workspace/TitleIdentity';
import { TitleActionMenu } from './TitleActionMenu';
import classes from './TitleDetail.module.css';
import { titleSectionPath } from './titleSections';

export function TitleHero({ title, titleId }: { title: TitleDetail; titleId: string }) {
  const [preview, setPreview] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const { canManageTitles } = usePermissions();
  const hero =
    title.artwork?.find((artwork) => artwork.role === 'Hero' && artwork.url) ??
    title.artwork?.find((artwork) => artwork.role === 'Backdrop' && artwork.url);
  const sources = useTitleSourceReferences(titleId);
  const { data: platform } = usePlatform(title.systemKey);
  const location = useLocation();
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
        <TitleIdentity
          title={title}
          platform={platform?.name}
          size="hero"
          nameAction={
            canManageTitles && (
              <Tooltip label="Edit title name">
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  component={Link}
                  to={`${titleSectionPath(titleId)}?field=Name`}
                  state={location.state}
                  aria-label="Edit title name"
                >
                  <IconEdit size={16} />
                </ActionIcon>
              </Tooltip>
            )
          }
        >
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
        </TitleIdentity>
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
    </Stack>
  );
}
