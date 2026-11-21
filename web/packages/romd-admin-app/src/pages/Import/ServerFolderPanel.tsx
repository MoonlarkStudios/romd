import {
  ActionIcon,
  Alert,
  Button,
  Group,
  Loader,
  NavLink,
  ScrollArea,
  Stack,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import type { ImportFolderListing } from '@romd/admin-api-client';
import { IconArrowUp, IconFolder, IconServer } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useImportFolders, type useImportServerFolder } from '../../hooks/api/useUpload';
import { RomImportOptions } from './RomImportOptions';

export function ServerFolderPanel({
  roots,
  active,
  start,
  refreshRoots,
  onStarted,
}: {
  roots: ImportFolderListing;
  active: boolean;
  start: ReturnType<typeof useImportServerFolder>;
  refreshRoots: () => void;
  onStarted: (jobId: string, name: string) => void;
}) {
  const [chosen, setChosen] = useState<string | null>();
  const currentPath = useRef<HTMLParagraphElement>(null);
  const [trackedOnly, setTrackedOnly] = useState(false);
  const [trackMatchedTitles, setTrackMatchedTitles] = useState(true);
  const [allowUnidentified, setAllowUnidentified] = useState(false);
  const pending = useRef<{
    intent: string;
    requestId: string;
  } | null>(null);
  const singleRoot = roots.folders.length === 1;
  const current = chosen === undefined ? (singleRoot ? roots.folders[0].path : null) : chosen;
  const listing = useImportFolders(current, active && current !== null);
  const shown = current === null ? roots : listing.data;
  const canGoUp = current !== null && (!!listing.data?.parentPath || !singleRoot);
  const locked = start.isPending;
  useEffect(() => {
    if (chosen !== undefined)
      currentPath.current?.focus({
        preventScroll: true,
      });
  }, [
    chosen,
  ]);
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
          setChosen(undefined);
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
            ROMD copies files from the selected folder and its subfolders.
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
            ref={currentPath}
            tabIndex={-1}
            size="sm"
            ff="monospace"
            style={{
              overflowWrap: 'anywhere',
            }}
          >
            {current ?? 'Allowed folders'}
          </Text>
        </Group>
        {current !== null && listing.isPending ? (
          <Loader
            size="sm"
            aria-label="Loading folders"
          />
        ) : current !== null && listing.isError ? (
          <Alert
            color="red"
            title="Could not open this folder"
          >
            <Stack gap="xs">
              <Text size="sm">{listing.error.message}</Text>
              <Group gap="xs">
                <Button
                  variant="subtle"
                  size="xs"
                  onClick={() => void listing.refetch()}
                >
                  Try again
                </Button>
                <Button
                  variant="subtle"
                  size="xs"
                  color="gray"
                  onClick={() => open(null)}
                >
                  Back to allowed folders
                </Button>
              </Group>
            </Stack>
          </Alert>
        ) : (
          shown && (
            <>
              {current === null && shown.folders.length === 0 && (
                <Alert title="No available server folders">
                  <Stack
                    gap="xs"
                    align="flex-start"
                  >
                    Check that the configured folders are mounted and readable by ROMD.
                    <Button
                      variant="default"
                      onClick={refreshRoots}
                    >
                      Refresh folders
                    </Button>
                  </Stack>
                </Alert>
              )}
              {!!shown.folders.length && (
                <ScrollArea.Autosize mah={260}>
                  <nav aria-label="Server folders">
                    {shown.folders.map((folder) => (
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
              )}
              {shown.truncated && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  Showing the first {shown.folders.length.toLocaleString()} folders.
                </Text>
              )}
              {current !== null && (
                <>
                  <Text
                    size="sm"
                    c="dimmed"
                  >
                    {shown.fileCount === 0 && shown.folders.length === 0
                      ? 'This folder is empty. Choose another folder.'
                      : `${shown.fileCount.toLocaleString()} ${shown.fileCount === 1 ? 'file' : 'files'} directly in this folder.`}
                  </Text>
                  <RomImportOptions
                    trackedOnly={trackedOnly}
                    setTrackedOnly={setTrackedOnly}
                    trackMatchedTitles={trackMatchedTitles}
                    setTrackMatchedTitles={setTrackMatchedTitles}
                    allowUnidentified={allowUnidentified}
                    setAllowUnidentified={setAllowUnidentified}
                    canKeepUnmatched
                    disabled={locked}
                  />
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
                        shown.fileCount === 0 && shown.folders.length === 0 && !start.isError
                      }
                      onClick={startImport}
                    >
                      {start.isError ? 'Try again' : 'Import this folder'}
                    </Button>
                  </Group>
                </>
              )}
            </>
          )
        )}
      </Stack>
    </section>
  );
}
