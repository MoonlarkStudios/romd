import { Alert, Button, Modal, Stack, Text } from '@mantine/core';
import { getRom } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { romKeys } from '../hooks/api/useRoms';
import { RomDownloadButtons } from './RomDownloadButtons';

/** Mounted only after a download action, so inventory lists do not fetch every detail. */
export function RomDownloadDialog({ romId, onClose }: { romId: string; onClose: () => void }) {
  const query = useQuery({
    queryKey: romKeys.detail(romId),
    staleTime: 0,
    queryFn: async ({ signal }) => {
      const response = await getRom({
        path: {
          romId,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Download formats unavailable');
      return response.data;
    },
  });
  return (
    <Modal
      opened
      onClose={onClose}
      title="Download formats"
      centered
      onClick={(event) => event.stopPropagation()}
    >
      {query.isPending ? (
        <Text size="sm">Loading download formats…</Text>
      ) : query.isError ? (
        <Alert
          color="red"
          title="Download formats unavailable"
        >
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry download formats
          </Button>
        </Alert>
      ) : (
        <Stack>
          <Text
            size="sm"
            style={{
              overflowWrap: 'anywhere',
            }}
          >
            {query.data.originalFilename}
          </Text>
          <RomDownloadButtons rom={query.data} />
        </Stack>
      )}
    </Modal>
  );
}
