import { Button, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  createDownloadTicket,
  getRom,
  type Rom,
  type RomDeliveryFormatDto,
} from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { IconDownload } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { romKeys } from '../hooks/api/useRoms';
import { formatBytes } from '../utils/format';
import { workspaceActionProps } from './Workspace/workspaceActions';

/** List projections fetch the detail contract before offering delivered formats. */
export function RomDownloadButtons({ rom }: { rom: Rom }) {
  const [pending, setPending] = useState<RomDeliveryFormatDto['format'] | null>(null);
  const query = useQuery({
    queryKey: romKeys.detail(rom.id),
    enabled: rom.formats == null,
    queryFn: async ({ signal }) => {
      const result = await getRom({
        path: {
          romId: rom.id,
        },
        signal,
      });
      if (result.error || !result.data) throw new Error('Download formats unavailable');
      return result.data;
    },
  });
  const formats = rom.formats == null ? query.data?.formats : rom.formats;
  const download = async (format: RomDeliveryFormatDto['format'], filename: string) => {
    if (pending) return;
    setPending(format);
    try {
      const result = await createDownloadTicket({
        body: {
          target: 'Rom',
          id: rom.id,
          format,
        },
      });
      if (result.error || !result.data) throw new Error('Download unavailable');
      startBrowserDownload(result.data.downloadUrl, filename);
    } catch {
      notifications.show({
        color: 'red',
        message: 'Download failed. Please try again.',
      });
    } finally {
      setPending(null);
    }
  };

  if (rom.formats == null && query.isPending) {
    return (
      <Button
        variant="light"
        loading
      >
        Loading download formats
      </Button>
    );
  }
  if (rom.formats == null && query.isError) {
    return (
      <Button
        variant="light"
        onClick={() => void query.refetch()}
      >
        Retry download formats
      </Button>
    );
  }
  if (formats === undefined) {
    return (
      <Button
        {...workspaceActionProps}
        variant="light"
        leftSection={<IconDownload size={16} />}
        loading={pending === 'original'}
        onClick={() => void download('original', rom.originalFilename)}
      >
        Download
      </Button>
    );
  }
  if (!formats?.length)
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No downloads available.
      </Text>
    );
  return (
    <Stack gap="xs">
      {formats.map((format) => {
        const available =
          format.state === 'available' &&
          !!format.filename &&
          format.sizeBytes != null &&
          !!format.sha256;
        return (
          <Stack
            key={format.format}
            gap={2}
          >
            <Button
              {...workspaceActionProps}
              variant="light"
              leftSection={<IconDownload size={16} />}
              disabled={!available || (pending != null && pending !== format.format)}
              loading={pending === format.format}
              onClick={() => {
                if (available && format.filename) void download(format.format, format.filename);
              }}
            >
              {format.state === 'available'
                ? `Download ${format.label}`
                : `${format.label} ${format.state}`}
            </Button>
            {format.filename && format.sizeBytes != null && (
              <Text
                size="xs"
                c="dimmed"
              >
                {format.filename} · {formatBytes(format.sizeBytes)}
              </Text>
            )}
          </Stack>
        );
      })}
    </Stack>
  );
}
