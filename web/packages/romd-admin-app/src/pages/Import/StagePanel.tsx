import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Group,
  Progress,
  ScrollArea,
  Select,
  Stack,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { IconArchive, IconFiles, IconFolderPlus, IconUpload, IconX } from '@tabler/icons-react';
import { useRef } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import { formatBytes, formatDuration, formatRate } from '../../utils/format';
import type { FileKind } from './classify';
import { RomImportOptions } from './RomImportOptions';
import { importFileIssue, type useImportSession } from './useImportSession';

const fileKinds: Record<FileKind, string> = {
  rom: 'ROM',
  archive: 'Archive',
  dat: 'DAT catalog',
  unknown: 'Other file',
};

export function StagePanel(props: ReturnType<typeof useImportSession>) {
  const openFiles = useRef<(() => void) | null>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const platforms = usePlatforms();
  const { canManageTitles } = usePermissions();
  const { staged, isStarting, uploadProgress: progress, retryPending } = props;
  const locked = isStarting || retryPending;
  return (
    <section>
      <Stack gap="md">
        <Title
          order={2}
          className={workspace.sectionHeading}
        >
          Files to import
        </Title>
        {!locked && (
          <Dropzone
            onDrop={props.addFiles}
            openRef={openFiles}
            activateOnClick={false}
            activateOnKeyboard={false}
            enablePointerEvents
            data-testid="import-dropzone"
          >
            <Stack
              align="center"
              justify="center"
              gap="sm"
              py="sm"
            >
              {staged.length === 0 && (
                <IconArchive
                  size={28}
                  stroke={1.5}
                  color="var(--mantine-primary-color-filled)"
                  aria-hidden
                />
              )}
              <Text ta="center">
                {staged.length
                  ? 'Drop more files here'
                  : 'Drop ROMs, archives, or DAT catalogs here'}
              </Text>
              <Group
                gap="xs"
                justify="center"
              >
                <Button
                  {...workspaceActionProps}
                  variant="default"
                  leftSection={<IconFiles size={16} />}
                  onClick={() => openFiles.current?.()}
                >
                  Browse files
                </Button>
                <Button
                  {...workspaceActionProps}
                  variant="default"
                  leftSection={<IconFolderPlus size={16} />}
                  onClick={() => folderInputRef.current?.click()}
                >
                  Browse folder
                </Button>
              </Group>
            </Stack>
          </Dropzone>
        )}
        <input
          ref={folderInputRef}
          type="file"
          multiple
          hidden
          // @ts-expect-error directory selection is supported by browsers but not React's input types
          webkitdirectory=""
          onChange={({ currentTarget }) => {
            if (currentTarget.files) props.addFiles(Array.from(currentTarget.files));
            currentTarget.value = '';
          }}
        />
        {!!staged.length && (
          <>
            <Group justify="space-between">
              <Text
                size="sm"
                c="dimmed"
              >
                {staged.length.toLocaleString()} {staged.length === 1 ? 'file' : 'files'} ·{' '}
                {formatBytes(props.totalBytes)}
              </Text>
              <Button
                variant="subtle"
                color="gray"
                disabled={isStarting}
                onClick={props.clearStaged}
              >
                {retryPending ? 'Discard remaining files' : 'Clear'}
              </Button>
            </Group>
            {props.stagingIssue && (
              <Alert color="orange">
                <Stack
                  align="flex-start"
                  gap="xs"
                >
                  {props.stagingIssue}
                  {props.invalidCount > 0 && (
                    <Button
                      variant="default"
                      size="xs"
                      disabled={locked}
                      onClick={props.removeInvalidFiles}
                    >
                      Remove {props.invalidCount.toLocaleString()} invalid{' '}
                      {props.invalidCount === 1 ? 'file' : 'files'}
                    </Button>
                  )}
                </Stack>
              </Alert>
            )}
            <ScrollArea.Autosize mah={280}>
              <Table
                layout="fixed"
                verticalSpacing="sm"
                aria-label="Staged files"
              >
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>File</Table.Th>
                    <Table.Th w={110}>Size</Table.Th>
                    <Table.Th w={44} />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {staged.map((entry) => (
                    <Table.Tr key={entry.id}>
                      <Table.Td>
                        <Text
                          size="sm"
                          style={{
                            overflowWrap: 'anywhere',
                          }}
                        >
                          {entry.file.webkitRelativePath || entry.file.name}
                        </Text>
                        <Badge
                          variant="light"
                          color="gray"
                          size="xs"
                        >
                          {fileKinds[entry.kind]}
                        </Badge>
                        {importFileIssue(entry.file) && (
                          <Text
                            size="xs"
                            c="red"
                          >
                            {importFileIssue(entry.file)}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>{formatBytes(entry.file.size)}</Table.Td>
                      <Table.Td>
                        <Tooltip label="Remove file">
                          <ActionIcon
                            variant="subtle"
                            color="gray"
                            disabled={locked}
                            aria-label={`Remove ${entry.file.webkitRelativePath || entry.file.name}`}
                            onClick={() => props.removeFile(entry.id)}
                          >
                            <IconX size={16} />
                          </ActionIcon>
                        </Tooltip>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea.Autosize>
            {!props.stagingIssue && !locked && props.planKind === 'individual' && (
              <Text
                size="sm"
                c="dimmed"
              >
                Each file will run as its own import. Review all {staged.length.toLocaleString()}{' '}
                jobs here after upload.
              </Text>
            )}
            <RomImportOptions
              trackedOnly={props.trackedOnly}
              setTrackedOnly={props.setTrackedOnly}
              trackMatchedTitles={props.trackMatchedTitles}
              setTrackMatchedTitles={props.setTrackMatchedTitles}
              allowUnidentified={props.allowUnidentified}
              setAllowUnidentified={props.setAllowUnidentified}
              canKeepUnmatched={canManageTitles}
              disabled={locked}
            />
            {props.hasDats && (
              <Select
                label="Assign catalogs to system"
                placeholder="Let ROMD route by header"
                value={props.defaultPlatformId}
                onChange={props.setDefaultPlatformId}
                data={(platforms.data ?? []).map((platform) => ({
                  value: platform.key,
                  label: platform.compactLabel
                    ? `${platform.name} (${platform.compactLabel})`
                    : platform.name,
                }))}
                searchable
                clearable
                disabled={locked || platforms.isLoading}
                maw={440}
              />
            )}
            {props.uploadError && (
              <Alert
                color="red"
                title="Upload interrupted"
              >
                {props.uploadError}
              </Alert>
            )}
            {progress && (
              <Stack
                gap="xs"
                role="status"
              >
                <Group justify="space-between">
                  <Text
                    size="sm"
                    fw={500}
                  >
                    {progress.phase === 'preparing'
                      ? 'Preparing files'
                      : progress.phase === 'accepting'
                        ? 'Accepting upload'
                        : 'Uploading'}
                    {progress.fileCount > 1
                      ? ` · ${progress.fileIndex} of ${progress.fileCount}`
                      : ''}
                  </Text>
                  <Button
                    variant="subtle"
                    color="gray"
                    size="xs"
                    onClick={props.cancelUpload}
                  >
                    Stop transfer
                  </Button>
                </Group>
                {progress.filename && (
                  <Text
                    size="sm"
                    style={{
                      overflowWrap: 'anywhere',
                    }}
                  >
                    {progress.filename}
                  </Text>
                )}
                <Progress
                  aria-label="Upload progress"
                  value={
                    progress.phase === 'uploading'
                      ? Math.min(100, (progress.loaded / Math.max(1, progress.total)) * 100)
                      : 100
                  }
                  animated
                  striped={progress.phase !== 'uploading'}
                  color="teal"
                  size="sm"
                />
                {progress.phase === 'uploading' && (
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {formatBytes(progress.loaded)} / {formatBytes(progress.total)} ·{' '}
                    {formatRate(progress.bytesPerSec)}
                    {Number.isFinite(progress.etaSeconds)
                      ? ` · ${formatDuration(progress.etaSeconds)} remaining`
                      : ''}
                  </Text>
                )}
              </Stack>
            )}
            <Group justify="flex-end">
              <Button
                {...workspaceActionProps}
                leftSection={<IconUpload size={16} />}
                loading={isStarting}
                disabled={!!props.stagingIssue && !retryPending}
                onClick={() => void props.startImport()}
              >
                {retryPending
                  ? 'Retry remaining uploads'
                  : `Start import (${staged.length.toLocaleString()})`}
              </Button>
            </Group>
          </>
        )}
      </Stack>
    </section>
  );
}
