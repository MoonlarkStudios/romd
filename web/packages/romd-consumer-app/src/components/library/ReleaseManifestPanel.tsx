import { Alert, Badge, Button, Group, Stack, Text } from '@mantine/core';
import type {
  ConsumerRelease,
  ConsumerReleaseManifest,
  ConsumerReleaseManifestFormat,
} from '@romd/consumer-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { IconDownload } from '@tabler/icons-react';
import { type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { useIssueReleaseManifest } from '../../hooks/useConsumerLibrary';
import { formatBytes } from '../../utils/format';
import { formatList } from '../../utils/library';
import classes from './ReleaseManifestPanel.module.css';

interface ReleaseManifestPanelProps {
  release: ConsumerRelease | null;
  playAction?: ReactNode;
  showSummary?: boolean;
  autoDownload?: boolean;
  onDownloadStarted?: () => void;
}

type DownloadChoice =
  | {
      kind: 'format';
      path?: string;
      format: ConsumerReleaseManifestFormat['format'];
    }
  | {
      kind: 'derived';
      path: string;
    };

type FileDownload = {
  choice: DownloadChoice;
  filename: string;
  sizeBytes: string | null | undefined;
  label: string;
  available: boolean;
};

/** Saving uses browser-managed delivery; playback retains its separate integrity checks. */
export function ReleaseManifestPanel({
  release,
  playAction,
  showSummary = true,
  autoDownload = false,
  onDownloadStarted,
}: ReleaseManifestPanelProps) {
  const { mutateAsync } = useIssueReleaseManifest();
  const [manifest, setManifest] = useState<ConsumerReleaseManifest | null>(null);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [filesOpen, setFilesOpen] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const retryChoice = useRef<DownloadChoice>({
    kind: 'format',
    format: 'original',
  });
  const autoStarted = useRef(false);

  useEffect(
    () => () => {
      controller.current?.abort();
      controller.current = null;
      autoStarted.current = false;
    },
    [],
  );

  const prepare = useCallback(
    async (
      download: boolean,
      choice: DownloadChoice = {
        kind: 'format',
        format: 'original',
      },
    ) => {
      if (!release || controller.current) return;
      retryChoice.current = choice;
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
        if (!download) {
          setStatus('');
          return;
        }
        const item = choice.path
          ? next.items.find((candidate) => candidate.relativePath === choice.path)
          : next.items.length === 1
            ? next.items[0]
            : undefined;
        if (!item) {
          setFilesOpen(true);
          setStatus('Choose a file to download. This release contains multiple files.');
          return;
        }
        if (choice.kind === 'format') {
          if (item.provenance !== 'Original') throw new Error('Format unavailable');
          const target = item.formats.find((candidate) => candidate.format === choice.format);
          if (
            !target ||
            target.state !== 'available' ||
            !target.contentGrant ||
            !target.filename ||
            target.sizeBytes == null ||
            !target.sha256
          )
            throw new Error('Format unavailable');
          startBrowserDownload(target.contentGrant.downloadUrl, target.filename);
        } else {
          if (
            item.provenance !== 'Derived' ||
            item.formats.length !== 0 ||
            !item.isAvailable ||
            !item.sha256 ||
            !item.contentGrant
          )
            throw new Error('File unavailable');
          startBrowserDownload(item.contentGrant.downloadUrl, item.relativePath.split('/').at(-1));
        }
        setStatus('Download started. Check your browser for progress.');
      } catch {
        if (!operation.signal.aborted) {
          setError('We couldn’t prepare or download this file. Try again.');
          setStatus('');
        }
      } finally {
        if (!operation.signal.aborted) setBusy(false);
        if (controller.current === operation) controller.current = null;
      }
    },
    [
      release,
      mutateAsync,
    ],
  );

  useEffect(() => {
    if (autoDownload && !autoStarted.current) {
      autoStarted.current = true;
      onDownloadStarted?.();
      void prepare(true);
    }
    if (!autoDownload) autoStarted.current = false;
  }, [
    autoDownload,
    onDownloadStarted,
    prepare,
  ]);

  if (!release) return <Text c="dimmed">No release is available yet.</Text>;
  const multipleFiles = (manifest?.items.length ?? 0) > 1;
  return (
    <Stack
      gap="md"
      className={showSummary ? classes.panel : undefined}
    >
      {showSummary && (
        <Stack gap="xs">
          <Group gap="xs">
            <Text fw={600}>{formatList(release.regions, release.name)}</Text>
            <Badge
              color={release.isComplete ? 'mint' : 'bronze'}
              variant="light"
            >
              {release.isComplete ? 'Available' : 'Missing files'}
            </Badge>
          </Group>
          <Text
            size="sm"
            c="dimmed"
          >
            {[
              formatList(release.languages, 'Language unspecified'),
              release.revision,
              formatBytes(release.sizeBytes),
            ]
              .filter(Boolean)
              .join(' · ')}
          </Text>
        </Stack>
      )}
      <Group gap="sm">
        {playAction}
        <Button
          variant="default"
          leftSection={<IconDownload size={16} />}
          loading={busy}
          onClick={() => (multipleFiles ? setFilesOpen(true) : void prepare(true))}
        >
          {multipleFiles ? 'Choose files' : 'Download'}
        </Button>
      </Group>
      <output aria-live="polite">
        {status && (
          <Text
            component="span"
            size="sm"
            c="dimmed"
          >
            {status}
          </Text>
        )}
      </output>
      {error && (
        <Alert
          color="red"
          title="Download unavailable"
        >
          <Stack gap="xs">
            <Text size="sm">{error}</Text>
            <Button
              variant="subtle"
              onClick={() => void prepare(true, retryChoice.current)}
              disabled={busy}
            >
              Retry
            </Button>
          </Stack>
        </Alert>
      )}
      <details
        className={classes.files}
        open={filesOpen}
        onToggle={(event) => {
          const open = event.currentTarget.open;
          setFilesOpen(open);
          if (open && !manifest && !busy) void prepare(false);
        }}
      >
        <summary>Files</summary>
        {filesOpen && (
          <Stack
            gap="md"
            mt="md"
          >
            {manifest?.items.flatMap((item) => {
              const downloads: FileDownload[] =
                item.provenance === 'Derived'
                  ? [
                      {
                        choice: {
                          kind: 'derived',
                          path: item.relativePath,
                        },
                        filename: item.relativePath,
                        sizeBytes: item.sizeBytes,
                        label: 'Download generated CUE',
                        available:
                          item.formats.length === 0 &&
                          item.isAvailable &&
                          !!item.sha256 &&
                          !!item.contentGrant,
                      },
                    ]
                  : item.formats.map((format) => ({
                      choice: {
                        kind: 'format',
                        path: item.relativePath,
                        format: format.format,
                      },
                      filename: format.filename ?? format.relativePath,
                      sizeBytes: format.sizeBytes,
                      label:
                        format.state === 'available'
                          ? `Download ${format.label}`
                          : `${format.label} ${format.state}`,
                      available:
                        format.state === 'available' &&
                        !!format.filename &&
                        format.sizeBytes != null &&
                        !!format.sha256 &&
                        !!format.contentGrant,
                    }));
              if (!downloads.length)
                return (
                  <Text
                    key={item.relativePath}
                    size="sm"
                    c="dimmed"
                  >
                    {item.relativePath} · No downloads available.
                  </Text>
                );
              return downloads.map((file) => {
                return (
                  <div
                    key={`${item.relativePath}:${file.label}`}
                    className={classes.fileRow}
                  >
                    <Stack
                      gap={2}
                      className={classes.fileInfo}
                    >
                      <Text
                        size="sm"
                        style={{
                          overflowWrap: 'anywhere',
                        }}
                      >
                        {file.filename}
                      </Text>
                      {file.sizeBytes != null && (
                        <Text
                          size="xs"
                          c="dimmed"
                        >
                          {formatBytes(file.sizeBytes)}
                        </Text>
                      )}
                    </Stack>
                    <Button
                      variant="default"
                      disabled={busy || !file.available}
                      onClick={() => void prepare(true, file.choice)}
                    >
                      {file.label}
                    </Button>
                  </div>
                );
              });
            })}
          </Stack>
        )}
      </details>
    </Stack>
  );
}
