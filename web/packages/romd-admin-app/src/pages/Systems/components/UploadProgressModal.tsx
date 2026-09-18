import { Button, Group, Modal, Progress, Stack, Text, ThemeIcon } from '@mantine/core';
import type { JobDto } from '@romd/admin-api-client';
import { IconCheck, IconX } from '@tabler/icons-react';

interface UploadProgressModalProps {
  opened: boolean;
  onClose: () => void;
  job: JobDto | undefined;
  isLoading: boolean;
}

export function UploadProgressModal({
  opened,
  onClose,
  job,
  isLoading,
}: UploadProgressModalProps) {
  const progress = job ? Number(job.progressPercent || 0) : 0;
  const isComplete = job?.isTerminal ?? false;
  const hasErrors = job?.hasErrors ?? false;

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title="Uploading DAT File"
      centered
      closeOnClickOutside={isComplete}
      closeOnEscape={isComplete}
      withCloseButton={isComplete}
    >
      <Stack gap="md">
        {isLoading && !job ? (
          <Text c="dimmed">Starting upload...</Text>
        ) : job ? (
          <>
            <Group gap="xs">
              <Text fw={500}>Phase:</Text>
              <Text>{job.phase}</Text>
            </Group>

            {job.currentItem && (
              <Group gap="xs">
                <Text fw={500}>Processing:</Text>
                <Text size="sm" c="dimmed" lineClamp={1}>
                  {job.currentItem}
                </Text>
              </Group>
            )}

            <Progress
              value={progress}
              size="lg"
              color={hasErrors ? 'red' : isComplete ? 'green' : 'blue'}
              animated={!isComplete}
            />

            <Text ta="center" size="sm" c="dimmed">
              {progress}%
            </Text>

            {isComplete && (
              <Group justify="center" gap="md">
                <ThemeIcon
                  size="xl"
                  radius="xl"
                  color={hasErrors ? 'red' : 'green'}
                >
                  {hasErrors ? <IconX size={24} /> : <IconCheck size={24} />}
                </ThemeIcon>
                <Text fw={500} c={hasErrors ? 'red' : 'green'}>
                  {hasErrors ? 'Completed with errors' : 'Upload complete'}
                </Text>
              </Group>
            )}

            {job.errors && job.errors.length > 0 && (
              <Stack gap="xs">
                <Text fw={500} c="red">
                  Errors:
                </Text>
                {job.errors.slice(0, 5).map((error, idx) => (
                  <Text key={idx} size="sm" c="red">
                    {error.item}: {error.message}
                  </Text>
                ))}
                {job.errors.length > 5 && (
                  <Text size="sm" c="dimmed">
                    ...and {job.errors.length - 5} more errors
                  </Text>
                )}
              </Stack>
            )}

            {isComplete && (
              <Button onClick={onClose} fullWidth>
                Close
              </Button>
            )}
          </>
        ) : null}
      </Stack>
    </Modal>
  );
}
