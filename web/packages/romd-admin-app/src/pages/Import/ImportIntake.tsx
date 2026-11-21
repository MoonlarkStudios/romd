import { Alert, Button, Collapse, Divider, Stack, Tabs } from '@mantine/core';
import { IconDeviceLaptop, IconServer } from '@tabler/icons-react';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useServerImportStatus, useStartServerImport } from '../../hooks/api/useServerImport';
import { useImportFolders, useImportServerFolder } from '../../hooks/api/useUpload';
import { usePermissions } from '../../hooks/usePermissions';
import { ServerFolderPanel } from './ServerFolderPanel';
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
  const [browse, setBrowse] = useState(false);
  const { canManageTitles } = usePermissions();
  const roots = useImportFolders(null, canManageTitles);
  const status = useServerImportStatus(canManageTitles, source === 'server');
  const startFolder = useImportServerFolder();
  const startInbox = useStartServerImport();
  const hasInbox = !!status.data?.directory;
  const hasServer = canManageTitles && (hasInbox || roots.isError || roots.data?.enabled === true);
  const active = hasServer ? source : 'device';
  const switchingDisabled = session.isStarting || startFolder.isPending || startInbox.isPending;
  const showFolders = !hasInbox || browse;
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
          <Stack gap="lg">
            {status.data?.directory && (
              <ServerImportFolder
                status={status.data}
                start={startInbox}
                onStarted={openReceipt}
              />
            )}
            {status.isError && (
              <Alert
                title="Could not load the configured import folder"
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
            {hasInbox && (
              <>
                <Divider />
                <Button
                  variant="subtle"
                  color="gray"
                  style={{
                    alignSelf: 'flex-start',
                  }}
                  disabled={switchingDisabled}
                  aria-expanded={browse}
                  aria-controls="one-off-server-import"
                  onClick={() => setBrowse((value) => !value)}
                >
                  {browse ? 'Hide server folder browser' : 'Browse server folders'}
                </Button>
              </>
            )}
            <Collapse
              id="one-off-server-import"
              expanded={showFolders}
              keepMounted
            >
              {roots.data?.enabled ? (
                <ServerFolderPanel
                  roots={roots.data}
                  active={active === 'server' && showFolders}
                  start={startFolder}
                  refreshRoots={() => void roots.refetch()}
                  onStarted={openReceipt}
                />
              ) : roots.isError ? (
                <Alert
                  title="Could not load server folders"
                  color="red"
                >
                  <Stack
                    gap="xs"
                    align="flex-start"
                  >
                    {roots.error.message}
                    <Button
                      variant="default"
                      loading={roots.isFetching}
                      onClick={() => void roots.refetch()}
                    >
                      Retry
                    </Button>
                  </Stack>
                </Alert>
              ) : null}
            </Collapse>
          </Stack>
        </Tabs.Panel>
      )}
    </Tabs>
  );
}
