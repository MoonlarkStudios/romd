import { Alert, Button, Stack, Tabs } from '@mantine/core';
import { IconDeviceLaptop, IconServer } from '@tabler/icons-react';
import { useState } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useImportFolders, useImportServerFolder } from '../../hooks/api/useUpload';
import { usePermissions } from '../../hooks/usePermissions';
import { ServerFolderPanel } from './ServerFolderPanel';
import { StagePanel } from './StagePanel';
import type { useImportSession } from './useImportSession';

export function ImportIntake({
  session,
  onStarted,
}: {
  session: ReturnType<typeof useImportSession>;
  onStarted: () => void;
}) {
  const [source, setSource] = useState('device');
  const { canManageTitles } = usePermissions();
  const roots = useImportFolders(null, canManageTitles);
  const startFolder = useImportServerFolder();
  const hasServer = canManageTitles && (roots.isError || roots.data?.enabled === true);
  const active = hasServer ? source : 'device';
  const switchingDisabled = session.isStarting || startFolder.isPending;

  return (
    <Tabs
      value={active}
      onChange={(next) => {
        if (next) setSource(next);
      }}
      className={workspace.panel}
    >
      {hasServer && (
        <Tabs.List
          mb="lg"
          aria-label="Import source"
        >
          <Tabs.Tab
            value="device"
            leftSection={<IconDeviceLaptop size={16} />}
            disabled={switchingDisabled && active !== 'device'}
          >
            This device
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
      <Tabs.Panel
        value="device"
        {...(!hasServer && {
          role: 'presentation',
          'aria-labelledby': undefined,
        })}
      >
        <StagePanel {...session} />
      </Tabs.Panel>
      {hasServer && (
        <Tabs.Panel value="server">
          {roots.data?.enabled ? (
            <ServerFolderPanel
              roots={roots.data}
              active={active === 'server'}
              start={startFolder}
              refreshRoots={() => void roots.refetch()}
              onStarted={(id, name) => {
                onStarted();
                setSource('device');
                session.reopenJob(id, name);
              }}
            />
          ) : (
            <Alert
              title="Could not load server folders"
              color="red"
            >
              <Stack
                gap="xs"
                align="flex-start"
              >
                {roots.error?.message}
                <Button
                  variant="default"
                  loading={roots.isFetching}
                  onClick={() => void roots.refetch()}
                >
                  Retry
                </Button>
              </Stack>
            </Alert>
          )}
        </Tabs.Panel>
      )}
    </Tabs>
  );
}
