import {
  Alert,
  Button,
  Group,
  List,
  Modal,
  ScrollArea,
  Skeleton,
  Stack,
  Text,
} from '@mantine/core';
import type { ServerImportRetryFileGroup } from '@romd/admin-api-client';
import { useServerImportRetryReview, useStartServerImport } from '../../hooks/api/useServerImport';
import { ApiClientError } from '../../hooks/api/useTaxonomy';

function FileGroup({ group, label }: { group: ServerImportRetryFileGroup; label: string }) {
  if (!group.count) return null;
  return (
    <Stack gap={4}>
      <Text
        size="sm"
        fw={600}
      >
        {group.count.toLocaleString()} {label}
      </Text>
      <List
        size="sm"
        styles={{
          item: {
            overflowWrap: 'anywhere',
          },
        }}
      >
        {group.examples.map((path) => (
          <List.Item key={path}>{path}</List.Item>
        ))}
      </List>
      {group.count > group.examples.length && (
        <Text
          size="xs"
          c="dimmed"
        >
          Showing {group.examples.length} of {group.count.toLocaleString()} files.
        </Text>
      )}
    </Stack>
  );
}

export function ServerImportRetryDialog({
  jobId,
  close,
  accepted,
}: {
  jobId: string;
  close: () => void;
  accepted: (retryJobId: string) => void;
}) {
  const review = useServerImportRetryReview(jobId);
  const start = useStartServerImport();
  const data = review.data;
  const refresh = () => {
    start.reset();
    void review.refetch();
  };

  return (
    <Modal
      opened
      onClose={close}
      title="Review remaining files"
      size="lg"
      closeOnEscape={!start.isPending}
      closeOnClickOutside={!start.isPending}
      withCloseButton={!start.isPending}
    >
      <Stack gap="md">
        {review.isPending && (
          <Skeleton
            h={100}
            aria-label="Checking original files"
          />
        )}
        {review.isError && <Alert color="red">{review.error.message}</Alert>}
        {start.error && <Alert color="orange">{start.error.message}</Alert>}
        {data && !review.isError && (
          <>
            {data.isBusy && (
              <Alert color="blue">
                An import is running from this folder. Check again when it finishes.
              </Alert>
            )}
            {data.ready.count === 0 && (
              <Text size="sm">No unchanged originals are ready to retry.</Text>
            )}
            <ScrollArea.Autosize mah="min(55vh, 480px)">
              <Stack
                gap="md"
                pr="xs"
              >
                <FileGroup
                  group={data.ready}
                  label="ready to retry"
                />
                <FileGroup
                  group={data.changed}
                  label="changed"
                />
                <FileGroup
                  group={data.missing}
                  label="missing"
                />
                <FileGroup
                  group={data.unavailable}
                  label="unavailable"
                />
              </Stack>
            </ScrollArea.Autosize>
            {data.changed.count > 0 && (
              <Text
                size="sm"
                c="dimmed"
              >
                Changed files can be picked up by a new folder check.
              </Text>
            )}
          </>
        )}
        <Group justify="end">
          <Button
            variant="default"
            onClick={close}
            disabled={start.isPending}
          >
            Cancel
          </Button>
          {(review.isError || start.isError || data?.isBusy || data?.ready.count === 0) && (
            <Button
              variant="light"
              onClick={refresh}
              loading={review.isFetching}
            >
              Refresh review
            </Button>
          )}
          <Button
            disabled={!data?.ready.count || data.isBusy || review.isFetching || review.isError}
            loading={start.isPending}
            onClick={() => {
              if (!data) return;
              start.mutate(
                {
                  retryJobId: jobId,
                  reviewedRevision: data.revision,
                },
                {
                  onSuccess: (result) => {
                    if ('jobId' in result) accepted(result.jobId);
                  },
                  onError: (error) => {
                    if (error instanceof ApiClientError && error.status === 409)
                      void review.refetch();
                  },
                },
              );
            }}
          >
            Queue retry
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
