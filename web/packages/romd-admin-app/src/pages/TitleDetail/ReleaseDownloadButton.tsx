import { Button, Group, NativeSelect, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  type ArchiveFormat,
  type CreateDownloadTicketRequest,
  createDownloadTicket,
  type TitleFileRequirement,
} from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { IconDownload } from '@tabler/icons-react';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { archiveFormatLabel } from '../../utils/archiveFormats';

export function ReleaseDownloadButton({
  files,
  formats,
  name,
  releaseId,
}: {
  files: TitleFileRequirement[];
  formats: ArchiveFormat[];
  name: string;
  releaseId?: string | null;
}) {
  const [pending, setPending] = useState(false);
  const [chosen, setChosen] =
    useState<NonNullable<CreateDownloadTicketRequest['format']>>('original');
  // A refreshed release can stop offering the chosen format; the ZIP then falls back to originals.
  const format = formats.some((choice) => choice.format === chosen) ? chosen : 'original';
  const available = files.filter((file) => file.isOwned && file.romFileId);
  const partial = files.some(
    (file) => file.status?.toLowerCase() !== 'nodump' && (!file.isOwned || !file.romFileId),
  );
  const label = partial ? 'Download available files' : 'Download ZIP';
  const explanation =
    available.length === 0
      ? 'No local files available to download.'
      : 'Download the available release files in one ZIP archive.';
  const download = async () => {
    if (pending) return;
    setPending(true);
    try {
      if (!releaseId) throw new Error('No release available.');
      const result = await createDownloadTicket({
        body: {
          target: 'Release',
          id: releaseId,
          format,
        },
      });
      if (result.error || !result.data) throw new Error('Download unavailable');
      startBrowserDownload(result.data.downloadUrl);
      notifications.show({
        message: 'Download started. Check your browser for progress.',
        color: 'teal',
      });
    } catch {
      notifications.show({
        color: 'red',
        message: 'Could not prepare the download. Refresh and try again.',
      });
    } finally {
      setPending(false);
    }
  };
  return (
    <Group
      gap="xs"
      wrap="nowrap"
    >
      {formats.length > 1 && (
        <NativeSelect
          aria-label={`ZIP format for ${name}`}
          size="xs"
          value={format}
          disabled={pending || !available.length || !releaseId}
          onChange={(event) => {
            const value = event.currentTarget.value;
            const choice = formats.find((offered) => offered.format === value);
            if (choice) setChosen(choice.format);
          }}
          data={formats.map((choice) => ({
            value: choice.format,
            label: archiveFormatLabel(choice),
          }))}
        />
      )}
      <Tooltip
        label={explanation}
        multiline
        w={280}
      >
        <span>
          <Button
            {...workspaceActionProps}
            size="xs"
            variant="subtle"
            leftSection={<IconDownload size={14} />}
            aria-label={`${label} for ${name}`}
            disabled={!available.length || !releaseId}
            loading={pending}
            onClick={() => void download()}
          >
            {label}
          </Button>
        </span>
      </Tooltip>
    </Group>
  );
}
