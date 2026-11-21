import {
  ActionIcon,
  Alert,
  Button,
  Checkbox,
  Group,
  Loader,
  NavLink,
  ScrollArea,
  Select,
  Stack,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { IconArrowUp, IconFolder, IconServer } from '@tabler/icons-react';
import { useRef, useState } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useImportFolders, useImportServerFolder } from '../../hooks/api/useUpload';
import { usePermissions } from '../../hooks/usePermissions';

/**
 * Imports a folder that is already on the server, for libraries too large to send through the
 * browser or a proxy. Managers pick from the operator's allowed roots; there is no typed path,
 * and the source is always copied, never moved. Hidden unless the operator enabled path import.
 */
export function ServerFolderPanel({
  disabled,
  onStarted,
}: {
  disabled: boolean;
  onStarted: (jobId: string, name: string) => void;
}) {
  const { canManageTitles } = usePermissions();
  const [chosen, setChosen] = useState<string | null>(null);
  const [trackedOnly, setTrackedOnly] = useState(false);
  const [trackMatchedTitles, setTrackMatchedTitles] = useState(true);
  const [allowUnidentified, setAllowUnidentified] = useState(false);
  const pending = useRef<{
    intent: string;
    requestId: string;
  } | null>(null);
  const roots = useImportFolders(null, canManageTitles);
  const rootFolders = roots.data?.folders ?? [];
  const singleRoot = rootFolders.length === 1;
  const current = chosen ?? (singleRoot ? rootFolders[0].path : null);
  const listing = useImportFolders(current, canManageTitles && !!roots.data?.enabled);
  const start = useImportServerFolder();

  if (!canManageTitles || !roots.data?.enabled) return null;

  const shown = current === null ? roots : listing;
  const canGoUp = current !== null && (!!listing.data?.parentPath || !singleRoot);
  const locked = disabled || start.isPending;
  const open = (path: string | null) => {
    start.reset();
    setChosen(path);
  };
  const startImport = () => {
    if (!current || locked) return;
    const options = {
      path: current,
      trackedOnly,
      archiveOnly: trackedOnly || !trackMatchedTitles,
      allowUnidentified: !trackedOnly && allowUnidentified,
    };
    const intent = JSON.stringify(options);
    const requestId =
      pending.current?.intent === intent ? pending.current.requestId : crypto.randomUUID();
    pending.current = {
      intent,
      requestId,
    };
    start.mutate(
      {
        ...options,
        requestId,
      },
      {
        onSuccess: (reference) => {
          pending.current = null;
          setChosen(null);
          onStarted(reference.jobId, current.split(/[\\/]/).filter(Boolean).at(-1) ?? current);
        },
      },
    );
  };

  return (
    <section aria-labelledby="server-folder-heading">
      <Stack gap="md">
        <Stack gap={4}>
          <Title
            order={2}
            id="server-folder-heading"
            className={workspace.sectionHeading}
          >
            Import from a server folder
          </Title>
          <Text
            size="sm"
            c="dimmed"
          >
            Files are copied into ROMD in the background.
          </Text>
        </Stack>
        <Group
          gap="xs"
          wrap="nowrap"
        >
          <Tooltip label={current === null ? 'Allowed folders' : 'Up one folder'}>
            <ActionIcon
              variant="default"
              aria-label="Up one folder"
              disabled={!canGoUp || locked}
              onClick={() => open(listing.data?.parentPath ?? null)}
            >
              <IconArrowUp size={16} />
            </ActionIcon>
          </Tooltip>
          <Text
            size="sm"
            ff="monospace"
            style={{
              overflowWrap: 'anywhere',
            }}
          >
            {current ?? 'Allowed folders'}
          </Text>
        </Group>
        {shown.isPending ? (
          <Loader
            size="sm"
            aria-label="Loading folders"
          />
        ) : shown.isError ? (
          <Alert
            color="red"
            title="Could not open this folder"
          >
            <Stack gap="xs">
              <Text size="sm">{shown.error.message}</Text>
              <Group gap="xs">
                <Button
                  variant="subtle"
                  size="xs"
                  onClick={() => void shown.refetch()}
                >
                  Try again
                </Button>
                {current !== null && (
                  <Button
                    variant="subtle"
                    size="xs"
                    color="gray"
                    onClick={() => open(null)}
                  >
                    Back to allowed folders
                  </Button>
                )}
              </Group>
            </Stack>
          </Alert>
        ) : (
          <>
            <ScrollArea.Autosize mah={260}>
              <nav aria-label="Server folders">
                {shown.data.folders.map((folder) => (
                  <NavLink
                    component="button"
                    type="button"
                    key={folder.path}
                    label={folder.name}
                    leftSection={
                      current === null ? <IconServer size={16} /> : <IconFolder size={16} />
                    }
                    disabled={locked}
                    onClick={() => open(folder.path)}
                  />
                ))}
              </nav>
            </ScrollArea.Autosize>
            {shown.data.truncated && (
              <Text
                size="xs"
                c="dimmed"
              >
                Showing the first {shown.data.folders.length.toLocaleString()} folders.
              </Text>
            )}
            {current !== null && (
              <>
                <Text size="sm">
                  {shown.data.fileCount.toLocaleString()}{' '}
                  {shown.data.fileCount === 1 ? 'file' : 'files'} directly in this folder.
                  {shown.data.folders.length > 0 && ' The import includes subfolders.'}
                </Text>
                <Select
                  label="ROMs to keep"
                  value={trackedOnly ? 'tracked' : 'all'}
                  onChange={(value) => setTrackedOnly(value === 'tracked')}
                  disabled={locked}
                  allowDeselect={false}
                  data={[
                    {
                      value: 'all',
                      label: 'All catalog matches',
                    },
                    {
                      value: 'tracked',
                      label: 'Tracked titles only',
                    },
                  ]}
                  maw={440}
                />
                <Group
                  gap="xl"
                  align="flex-start"
                >
                  <Checkbox
                    label="Track matched titles"
                    checked={!trackedOnly && trackMatchedTitles}
                    onChange={(event) => setTrackMatchedTitles(event.currentTarget.checked)}
                    disabled={locked || trackedOnly}
                  />
                  <Checkbox
                    label="Keep unmatched files"
                    checked={!trackedOnly && allowUnidentified}
                    onChange={(event) => setAllowUnidentified(event.currentTarget.checked)}
                    disabled={locked || trackedOnly}
                  />
                </Group>
                <Text
                  size="sm"
                  c="dimmed"
                >
                  {trackedOnly
                    ? 'Files outside the tracked selection are skipped.'
                    : allowUnidentified
                      ? 'Unmatched files go to Needs attention.'
                      : 'ROMs without a catalog match are skipped.'}
                </Text>
                {start.isError && (
                  <Alert
                    color="red"
                    title="Could not confirm the import"
                  >
                    {start.error.message}
                  </Alert>
                )}
                <Group justify="flex-end">
                  <Button
                    {...workspaceActionProps}
                    leftSection={<IconFolder size={16} />}
                    loading={start.isPending}
                    disabled={
                      disabled ||
                      (shown.data.fileCount === 0 &&
                        shown.data.folders.length === 0 &&
                        !start.isError)
                    }
                    onClick={startImport}
                  >
                    {start.isError ? 'Try again' : 'Import this folder'}
                  </Button>
                </Group>
              </>
            )}
          </>
        )}
      </Stack>
    </section>
  );
}
