import { Alert, Badge, Button, Group, Stack, Text } from '@mantine/core';
import type { ConsumerReleaseDto, ConsumerReleaseManifestDto } from '@romd/consumer-api-client';
import { IconDownload } from '@tabler/icons-react';
import { type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { useIssueReleaseManifest } from '../../hooks/useConsumerLibrary';
import { revokeVerifiedDownload, saveVerifiedDownload, type VerifiedDownload, verifyManifestItemDownload } from '../../services/downloadVerification';
import { formatBytes } from '../../utils/format';
import { formatList } from '../../utils/library';
import classes from './ReleaseManifestPanel.module.css';

interface ReleaseManifestPanelProps {
  release: ConsumerReleaseDto | null;
  playAction?: ReactNode;
  showSummary?: boolean;
  autoDownload?: boolean;
  onDownloadStarted?: () => void;
}

/** Delivery authorization and integrity checks are implementation details of Download. */
export function ReleaseManifestPanel({ release, playAction, showSummary = true, autoDownload = false, onDownloadStarted }: ReleaseManifestPanelProps) {
  const { mutateAsync } = useIssueReleaseManifest();
  const [manifest, setManifest] = useState<ConsumerReleaseManifestDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [filesOpen, setFilesOpen] = useState(false);
  const [saved, setSaved] = useState<Record<string, VerifiedDownload>>({});
  const controller = useRef<AbortController | null>(null);
  const retained = useRef<Record<string, VerifiedDownload>>({});
  const retryPath = useRef<string | undefined>(undefined);
  const autoStarted = useRef(false);

  useEffect(() => () => {
    controller.current?.abort();
    controller.current = null;
    autoStarted.current = false;
    Object.values(retained.current).forEach(revokeVerifiedDownload);
  }, []);

  const prepare = useCallback(async (download: boolean, path?: string) => {
    if (!release || controller.current) return;
    retryPath.current = path;
    const operation = new AbortController();
    controller.current = operation;
    setBusy(true);
    setError(null);
    setStatus('Preparing…');
    try {
      // Issue fresh authorization for every attempt; no manual refresh step.
      const next = await mutateAsync(release.id);
      if (operation.signal.aborted) return;
      if (!next) throw new Error('Release unavailable');
      setManifest(next);
      if (!download) { setStatus(''); return; }
      const item = path ? next.items.find(candidate => candidate.relativePath === path)
        : next.items.length === 1 ? next.items[0] : undefined;
      if (!item) {
        setFilesOpen(true);
        setStatus('Choose a file to download. This release contains multiple files.');
        return;
      }
      if (!item.isAvailable) throw new Error('File unavailable');
      const result = await verifyManifestItemDownload(item, operation.signal, phase => {
        if (!operation.signal.aborted) setStatus(phase === 'downloading' ? 'Downloading…' : 'Checking…');
      });
      if (operation.signal.aborted) {
        if (result.status === 'verified') revokeVerifiedDownload(result.file);
        return;
      }
      if (result.status !== 'verified') throw new Error('Download failed');
      const previous = retained.current[item.relativePath];
      if (previous) revokeVerifiedDownload(previous);
      retained.current[item.relativePath] = result.file;
      setSaved({ ...retained.current });
      saveVerifiedDownload(result.file);
      setStatus('File checked and sent to your browser. If saving did not start, use Save file.');
    } catch {
      if (!operation.signal.aborted) {
        setError('We couldn’t prepare or download this file. Try again.');
        setStatus('');
      }
    } finally {
      if (!operation.signal.aborted) setBusy(false);
      if (controller.current === operation) controller.current = null;
    }
  }, [release, mutateAsync]);

  useEffect(() => {
    if (autoDownload && !autoStarted.current) {
      autoStarted.current = true;
      onDownloadStarted?.();
      void prepare(true);
    }
    if (!autoDownload) autoStarted.current = false;
  }, [autoDownload, onDownloadStarted, prepare]);

  if (!release) return <Text c="dimmed">No release is available yet.</Text>;
  const multipleFiles = (manifest?.items.length ?? 0) > 1;
  return <Stack gap="md" className={showSummary ? classes.panel : undefined}>
    {showSummary && <Stack gap="xs"><Group gap="xs">
      <Text fw={600}>{formatList(release.regions, release.name)}</Text>
      <Badge color={release.isComplete ? 'mint' : 'bronze'} variant="light">{release.isComplete ? 'Available' : 'Missing files'}</Badge>
    </Group>
    <Text size="sm" c="dimmed">{[formatList(release.languages, 'Language unspecified'), release.revision, formatBytes(release.sizeBytes)].filter(Boolean).join(' · ')}</Text></Stack>}
    <Group gap="sm">
      {playAction}
      <Button variant="default" leftSection={<IconDownload size={16} />} loading={busy} onClick={() => multipleFiles ? setFilesOpen(true) : void prepare(true)}>{multipleFiles ? 'Choose files' : 'Download'}</Button>
      {!multipleFiles && !filesOpen && Object.values(saved).map(file => <Button key={file.relativePath} variant="subtle" onClick={() => saveVerifiedDownload(file)}>Save file</Button>)}
    </Group>
    <div role="status" aria-live="polite">{status && <Text size="sm" c="dimmed">{status}</Text>}</div>
    {error && <Alert color="red" title="Download unavailable"><Stack gap="xs"><Text size="sm">{error}</Text><Button variant="subtle" onClick={() => void prepare(true, retryPath.current)} disabled={busy}>Retry</Button></Stack></Alert>}
    <details className={classes.files} open={filesOpen} onToggle={event => {
      const open = event.currentTarget.open;
      setFilesOpen(open);
      if (open && !manifest && !busy) void prepare(false);
    }}>
      <summary>Files</summary>
      {filesOpen && <Stack gap="md" mt="md">
        {manifest?.items.map(item => <div key={item.relativePath} className={classes.fileRow}>
          <Stack gap={2} className={classes.fileInfo}><Text size="sm" style={{ overflowWrap: 'anywhere' }}>{item.relativePath}</Text><Text size="xs" c="dimmed">{formatBytes(item.sizeBytes)}{!item.isAvailable && ' · Missing file'}</Text></Stack>
          {saved[item.relativePath] ? <Button variant="subtle" onClick={() => saveVerifiedDownload(saved[item.relativePath])}>Save file</Button> : <Button variant="default" disabled={busy || !item.isAvailable} onClick={() => void prepare(true, item.relativePath)}>Download file</Button>}
        </div>)}
      </Stack>}
    </details>
  </Stack>;
}
