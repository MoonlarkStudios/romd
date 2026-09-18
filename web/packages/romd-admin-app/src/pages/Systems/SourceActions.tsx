import { ActionIcon, Menu } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { Dat, SourceLifecycleStatus } from '@romd/admin-api-client';
import { IconDots, IconDownload, IconSettings, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { downloadDatSource } from '../../hooks/api/useDats';
import { usePermissions } from '../../hooks/usePermissions';
import { ConfirmSourceStatusModal } from './ConfirmSourceStatusModal';

export function sourcePath(systemKey: string, dat: Dat) {
  return `/systems/${systemKey}/sources/${dat.sourceId ?? dat.id}`;
}

export function SourceActions({ dat, path, manual = false, lifecycle = false }: { dat: Dat; path: string; manual?: boolean; lifecycle?: boolean }) {
  const { canManageSources } = usePermissions();
  const [status, setStatus] = useState<SourceLifecycleStatus | 'Delete' | null>(null);
  return <>
    <Menu position="bottom-end"><Menu.Target><ActionIcon aria-label={`Actions for ${dat.name}`} title="Source actions" variant="subtle" color="gray"><IconDots size={18} /></ActionIcon></Menu.Target>
      <Menu.Dropdown>
        <Menu.Item component={Link} to={`${path}?tab=updates`}>Updates</Menu.Item>
        <Menu.Item leftSection={<IconDownload size={16} />} onClick={() => void downloadDatSource(dat).catch(() => notifications.show({ color: 'red', message: 'Could not download the installed DAT.' }))}>Download installed DAT</Menu.Item>
        {manual && <Menu.Item component={Link} to={`${path}?tab=updates`} leftSection={<IconUpload size={16} />}>Upload new version</Menu.Item>}
        {!lifecycle && <Menu.Item component={Link} to={`${path}?tab=settings`} leftSection={<IconSettings size={16} />}>Source settings</Menu.Item>}
        {lifecycle && canManageSources && <>
          <Menu.Divider />
          {(['Active', 'Discontinued', 'Disabled'] as const).filter((value) => value !== dat.sourceStatus).map((value) => <Menu.Item key={value} color={value === 'Active' ? undefined : 'orange'} onClick={() => setStatus(value)}>{value === 'Active' ? 'Re-enable source' : value === 'Discontinued' ? 'Mark discontinued' : 'Disable source'}</Menu.Item>)}
          <Menu.Item color="red" onClick={() => setStatus('Delete')}>Delete source permanently</Menu.Item>
        </>}
      </Menu.Dropdown>
    </Menu>
    <ConfirmSourceStatusModal dat={dat} targetStatus={status} onClose={() => setStatus(null)} />
  </>;
}
