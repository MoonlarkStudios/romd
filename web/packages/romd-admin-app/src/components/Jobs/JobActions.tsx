import { ActionIcon, Menu } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { JobDto } from '@romd/admin-api-client';
import {
  IconArchive,
  IconDotsVertical,
  IconDownload,
  IconPlayerStop,
  IconRefresh,
} from '@tabler/icons-react';
import { useState } from 'react';
import { downloadExportFile } from '../../hooks/api/useExport';
import { usePermissions } from '../../hooks/usePermissions';
import { canPerformJobAction } from './jobActionEligibility';
import { getJobTitle } from './jobTypeRegistry';

export interface JobActionsProps {
  job: JobDto;
  onCancel: (job: JobDto) => void;
  onArchive: (job: JobDto) => void;
  onRetry: (job: JobDto) => void;
  retryingId: string | null;
  actionsPending: boolean;
}

export function JobActions({
  job,
  onCancel,
  onArchive,
  onRetry,
  actionsPending,
  retryingId,
}: JobActionsProps) {
  const [downloading, setDownloading] = useState(false);
  const { hasRole } = usePermissions();
  const manager = hasRole('Manager');
  const admin = hasRole('Admin');
  const archive = canPerformJobAction(job, 'archive', manager, admin);
  const cancel = canPerformJobAction(job, 'cancel', manager, admin);
  const retry = canPerformJobAction(job, 'retry', manager, admin);
  const download = job.isTerminal && job.jobType === 'export' && job.hasDownload;
  const prepareDownload = async () => {
    if (downloading) return;
    setDownloading(true);
    try {
      await downloadExportFile(job.id);
    } catch {
      notifications.show({
        title: 'Download failed',
        message: 'Could not download export file.',
        color: 'red',
      });
    } finally {
      setDownloading(false);
    }
  };
  if (!archive && !cancel && !retry && !download) return null;
  return (
    <Menu
      shadow="md"
      width={190}
      position="bottom-end"
    >
      <Menu.Target>
        <ActionIcon
          variant="subtle"
          color="gray"
          aria-label={`Actions for ${getJobTitle(job)}`}
          disabled={actionsPending || retryingId === job.id || downloading}
        >
          <IconDotsVertical size={16} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        {download && (
          <Menu.Item
            leftSection={<IconDownload size={14} />}
            onClick={() => void prepareDownload()}
          >
            Download
          </Menu.Item>
        )}
        {retry && (
          <Menu.Item
            leftSection={<IconRefresh size={14} />}
            onClick={() => onRetry(job)}
          >
            {job.jobType === 'media-reconciliation'
              ? 'Retry media'
              : `Retry ${job.jobType === 'bulk_enrichment' ? Number(job.failedCount) : 0} failed`}
          </Menu.Item>
        )}
        {cancel && (
          <Menu.Item
            leftSection={<IconPlayerStop size={14} />}
            onClick={() => onCancel(job)}
          >
            Cancel
          </Menu.Item>
        )}
        {archive && (
          <Menu.Item
            leftSection={<IconArchive size={14} />}
            onClick={() => onArchive(job)}
          >
            Archive
          </Menu.Item>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}
