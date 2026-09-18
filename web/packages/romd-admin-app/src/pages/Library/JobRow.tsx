import {
  ActionIcon,
  Badge,
  Group,
  Loader,
  Progress,
  Text,
  ThemeIcon,
  Tooltip,
} from '@mantine/core';
import type { JobDto, JobDtoUploadJobDto } from '@romd/admin-api-client';
import { IconCheck, IconX } from '@tabler/icons-react';

function isUploadJob(job: JobDto): job is JobDtoUploadJobDto {
  return job.jobType === 'upload';
}

export interface JobRowProps {
  /** Job data */
  job: JobDto;
  /** Whether this row is selected */
  isSelected?: boolean;
  /** Callback when cancel is clicked */
  onCancel?: (jobId: string) => void;
}

/**
 * Job row component for inline display in the virtual list.
 * Shows job progress with status badges and progress bar.
 */
export function JobRow({ job, isSelected = false, onCancel }: JobRowProps) {
  const isComplete = job.isTerminal ?? false;
  const hasErrors = job.hasErrors ?? false;
  const progress = Number(job.progressPercent || 0);

  return (
    <Group
      gap="sm"
      wrap="nowrap"
      px="sm"
      h="100%"
      style={{
        borderBottom: '1px solid var(--mantine-color-gray-light)',
        backgroundColor: isSelected
          ? 'var(--mantine-color-blue-light)'
          : 'var(--mantine-color-blue-light)',
        opacity: isSelected ? 1 : 0.9,
      }}
    >
      {/* Status icon */}
      {isComplete ? (
        <ThemeIcon
          size="sm"
          radius="xl"
          color={hasErrors ? 'red' : 'green'}
          variant="light"
        >
          {hasErrors ? <IconX size={12} /> : <IconCheck size={12} />}
        </ThemeIcon>
      ) : (
        <Loader size="xs" color="blue" />
      )}

      {/* Job info */}
      <Group gap="xs" wrap="nowrap" flex={1} miw={0}>
        <Text fw={500} size="sm">
          {isComplete
            ? hasErrors
              ? 'Completed with errors'
              : 'Upload complete'
            : 'Processing...'}
        </Text>

        {job.currentItem && !isComplete && (
          <Text size="xs" c="dimmed" truncate="end" flex={1} miw={0}>
            {job.currentItem}
          </Text>
        )}
      </Group>

      {/* Progress bar */}
      <Group gap="xs" wrap="nowrap" w={150}>
        <Progress
          value={progress}
          size="sm"
          flex={1}
          color={hasErrors ? 'red' : isComplete ? 'green' : 'blue'}
          animated={!isComplete}
        />
        <Text size="xs" c="dimmed" w={35} ta="right">
          {progress}%
        </Text>
      </Group>

      {/* Badges */}
      <Group gap={4} wrap="nowrap">
        {isUploadJob(job) && (
          <>
            {job.romsIngested && Number(job.romsIngested) > 0 && (
              <Tooltip label="ROMs ingested">
                <Badge size="xs" variant="light" color="green">
                  +{job.romsIngested}
                </Badge>
              </Tooltip>
            )}
            {job.romsDeduplicated && Number(job.romsDeduplicated) > 0 && (
              <Tooltip label="Duplicates skipped">
                <Badge size="xs" variant="light" color="gray">
                  ={job.romsDeduplicated}
                </Badge>
              </Tooltip>
            )}
            {job.datsSucceeded && Number(job.datsSucceeded) > 0 && (
              <Tooltip label="DAT files processed">
                <Badge size="xs" variant="light" color="orange">
                  DAT:{job.datsSucceeded}
                </Badge>
              </Tooltip>
            )}
          </>
        )}
        {job.errors && job.errors.length > 0 && (
          <Tooltip
            label={job.errors.slice(0, 3).join('\n')}
            multiline
            w={300}
          >
            <Badge size="xs" variant="light" color="red">
              {job.errors.length} error{job.errors.length > 1 ? 's' : ''}
            </Badge>
          </Tooltip>
        )}
      </Group>

      {/* Cancel button (only for active jobs) */}
      {!isComplete && onCancel && (
        <Tooltip label="Cancel">
          <ActionIcon
            variant="subtle"
            color="red"
            size="sm"
            onClick={(e) => {
              e.stopPropagation();
              onCancel(job.id);
            }}
          >
            <IconX size={14} />
          </ActionIcon>
        </Tooltip>
      )}
    </Group>
  );
}

export default JobRow;
