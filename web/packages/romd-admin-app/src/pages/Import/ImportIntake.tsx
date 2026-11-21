import { Alert, Button, Tabs } from '@mantine/core';
import { IconDeviceLaptop, IconServer } from '@tabler/icons-react';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useServerImportStatus, useStartServerImport } from '../../hooks/api/useServerImport';
import { usePermissions } from '../../hooks/usePermissions';
import { ServerImportFolder } from './ServerImportFolder';
import { StagePanel } from './StagePanel';
import type { useImportSession } from './useImportSession';

export function ImportIntake({
  session,
  onStarted,
}: {
  session: ReturnType<typeof useImportSession>;
  onStarted: () => void;
}) {
  const [params] = useSearchParams();
  const [source, setSource] = useState(params.get('source') === 'server' ? 'server' : 'device');
  const { canManageTitles } = usePermissions();
  const status = useServerImportStatus(canManageTitles, source === 'server');
  const start = useStartServerImport();
  const serverFolder = canManageTitles && status.data?.directory ? status.data : undefined;
  const active = serverFolder ? source : 'device';
  const switchingDisabled = session.isStarting || start.isPending;
  const openReceipt = (id: string, name?: string) => {
    onStarted();
    setSource('device');
    session.reopenJob(id, name);
  };

  return (
    <Tabs
      value={active}
      onChange={(next) => {
        if (next) setSource(next);
      }}
      className={workspace.panel}
    >
      {serverFolder && (
        <Tabs.List
          mb="lg"
          aria-label="Import source"
        >
          <Tabs.Tab
            value="device"
            leftSection={<IconDeviceLaptop size={16} />}
            disabled={switchingDisabled && active !== 'device'}
          >
            Upload
          </Tabs.Tab>
          <Tabs.Tab
            value="server"
            leftSection={<IconServer size={16} />}
            disabled={switchingDisabled && active !== 'server'}
          >
            Server folder
          </Tabs.Tab>
        </Tabs.List>
      )}
      {canManageTitles && status.isError && (!serverFolder || active === 'server') && (
        <Alert
          mb="lg"
          title="Could not check the server import folder"
          color="red"
        >
          <Button
            variant="default"
            loading={status.isFetching}
            onClick={() => void status.refetch()}
          >
            Retry folder status
          </Button>
        </Alert>
      )}
      <Tabs.Panel
        value="device"
        {...(!serverFolder && {
          role: 'presentation',
          'aria-labelledby': undefined,
        })}
      >
        <StagePanel {...session} />
      </Tabs.Panel>
      {serverFolder && (
        <Tabs.Panel value="server">
          <ServerImportFolder
            status={serverFolder}
            start={start}
            onStarted={openReceipt}
          />
        </Tabs.Panel>
      )}
    </Tabs>
  );
}
