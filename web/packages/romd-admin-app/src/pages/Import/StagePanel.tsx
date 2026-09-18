import { ActionIcon, Alert, Badge, Button, Checkbox, Group, Progress, ScrollArea, Select, Stack, Table, Text, Title, Tooltip } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { IconArchive, IconFiles, IconFolderPlus, IconUpload, IconX } from '@tabler/icons-react';
import { useRef } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import { formatBytes, formatDuration, formatRate } from '../../utils/format';
import type { useImportSession } from './useImportSession';

export function StagePanel(props: ReturnType<typeof useImportSession>) {
  const filesInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);
  const platforms = usePlatforms();
  const { canManageTitles } = usePermissions();
  const { staged, isStarting, uploadProgress: progress, retryPending } = props;
  const locked = isStarting || retryPending;
  const handleInput = (input: HTMLInputElement) => {
    if (input.files) props.addFiles(Array.from(input.files));
    input.value = '';
  };
  return <section><Stack gap="md">
    <Title order={2} className={workspace.sectionHeading}>Files to import</Title>
    <Dropzone disabled={locked} onDrop={props.addFiles} data-testid="import-dropzone" styles={{ root: { minHeight: staged.length ? 110 : 170 } }}>
      <Stack align="center" justify="center" gap="xs" mih={staged.length ? 78 : 138} style={{ pointerEvents: 'none' }}>
        <IconArchive size={32} stroke={1.5} color="var(--mantine-color-teal-6)" />
        <Text fw={500}>{staged.length ? 'Add files' : 'Drop archives or ROM files'}</Text>
      </Stack>
    </Dropzone>
    <Group gap="xs">
      <Button {...workspaceActionProps} variant="default" disabled={locked} leftSection={<IconFiles size={16} />} onClick={() => filesInputRef.current?.click()}>Browse files</Button>
      <Button {...workspaceActionProps} variant="default" disabled={locked} leftSection={<IconFolderPlus size={16} />} onClick={() => folderInputRef.current?.click()}>Browse folder</Button>
      <input ref={filesInputRef} type="file" multiple hidden onChange={(event) => handleInput(event.currentTarget)} />
      <input ref={folderInputRef} type="file" multiple hidden
        // @ts-expect-error directory selection is supported by browsers but not React's input types
        webkitdirectory="" onChange={(event) => handleInput(event.currentTarget)} />
    </Group>
    {!!staged.length && <>
      <Group justify="space-between"><Text size="sm" c="dimmed">{staged.length.toLocaleString()} files · {formatBytes(staged.reduce((sum, entry) => sum + entry.file.size, 0))}</Text>
        <Button variant="subtle" color="gray" disabled={isStarting} onClick={props.clearStaged}>Clear</Button></Group>
      <ScrollArea.Autosize mah={280}>
        <Table layout="fixed" verticalSpacing="sm" aria-label="Staged files"><Table.Thead><Table.Tr><Table.Th>File</Table.Th><Table.Th w={110}>Size</Table.Th><Table.Th w={44} /></Table.Tr></Table.Thead>
          <Table.Tbody>{staged.map((entry) => <Table.Tr key={entry.id}>
            <Table.Td><Text size="sm" style={{ overflowWrap: 'anywhere' }}>{entry.file.webkitRelativePath || entry.file.name}</Text><Badge variant="light" color="gray" size="xs">{entry.kind === 'dat' ? 'DAT catalog' : entry.kind}</Badge></Table.Td>
            <Table.Td>{formatBytes(entry.file.size)}</Table.Td>
            <Table.Td><Tooltip label="Remove file"><ActionIcon variant="subtle" color="gray" disabled={locked} aria-label={`Remove ${entry.file.name}`} onClick={() => props.removeFile(entry.id)}><IconX size={16} /></ActionIcon></Tooltip></Table.Td>
          </Table.Tr>)}</Table.Tbody>
        </Table>
      </ScrollArea.Autosize>
      <Group gap="xl" align="flex-start">
        <Tooltip label="Track titles matched by the imported ROMs"><Checkbox label="Track matched titles" checked={props.trackMatchedTitles} onChange={(event) => props.setTrackMatchedTitles(event.currentTarget.checked)} disabled={locked} /></Tooltip>
        {canManageTitles && <Tooltip label="Store files without a DAT match in Needs attention"><Checkbox label="Keep unmatched files" checked={props.allowUnidentified} onChange={(event) => props.setAllowUnidentified(event.currentTarget.checked)} disabled={locked} /></Tooltip>}
      </Group>
      {props.hasDats && <Select label="Assign catalogs to system" placeholder="Let ROMD route by header" value={props.defaultPlatformId} onChange={props.setDefaultPlatformId}
        data={(platforms.data ?? []).map((platform) => ({ value: platform.key, label: platform.compactLabel ? `${platform.name} (${platform.compactLabel})` : platform.name }))}
        searchable clearable disabled={locked || platforms.isLoading} maw={440} />}
      {props.uploadError && <Alert color="red" title="Upload interrupted">{props.uploadError}</Alert>}
      {progress && <Stack gap="xs" role="status">
        <Group justify="space-between"><Text size="sm" fw={500}>{progress.phase === 'preparing' ? 'Preparing files' : progress.phase === 'accepting' ? 'Accepting upload' : 'Uploading'}{progress.fileCount > 1 ? ` · ${progress.fileIndex} of ${progress.fileCount}` : ''}</Text>
          <Button variant="subtle" color="gray" size="xs" onClick={props.cancelUpload}>Stop transfer</Button></Group>
        {progress.filename && <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{progress.filename}</Text>}
        <Progress aria-label="Upload progress" value={progress.phase === 'uploading' ? Math.min(100, progress.loaded / Math.max(1, progress.total) * 100) : 100} animated striped={progress.phase !== 'uploading'} color="teal" size="sm" />
        {progress.phase === 'uploading' && <Text size="xs" c="dimmed">{formatBytes(progress.loaded)} / {formatBytes(progress.total)} · {formatRate(progress.bytesPerSec)}{Number.isFinite(progress.etaSeconds) ? ` · ${formatDuration(progress.etaSeconds)} remaining` : ''}</Text>}
      </Stack>}
      <Group justify="flex-end"><Button {...workspaceActionProps} leftSection={<IconUpload size={16} />} loading={isStarting} onClick={() => void props.startImport()}>{retryPending ? 'Retry remaining uploads' : `Start import (${staged.length.toLocaleString()})`}</Button></Group>
    </>}
  </Stack></section>;
}
