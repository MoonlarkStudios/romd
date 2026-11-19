import {
  Alert,
  Button,
  Code,
  Group,
  Loader,
  Modal,
  SimpleGrid,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type {
  Dat,
  DatCatalogSubscription,
  DatReplacementPreview,
  JobReference,
} from '@romd/admin-api-client';
import {
  applyDatSubscription,
  createDatReplacement,
  getDatSubscriptionPreview,
  previewDatReplacement,
} from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { DatChangeBrowser, type DatChangeFilter } from './DatChangeBrowser';
import classes from './DatReview.module.css';
import { DatReviewVersions } from './DatReviewVersions';

function percentage(count: number, baseline: number | undefined) {
  if (count === 0 || !baseline) return undefined;
  const percent = (100 * count) / baseline;
  return `${percent < 0.1 ? '<0.1' : percent.toFixed(1)}% of current`;
}

function DatReviewSummary({
  preview,
  onViewChanges,
}: {
  preview: DatReplacementPreview;
  onViewChanges: (change: DatChangeFilter) => void;
}) {
  const changes = [
    {
      kind: 'Removed',
      entries: preview.entriesRemoved,
      files: preview.filesRemoved,
      priority: preview.entriesRemoved > 0 || preview.filesRemoved > 0,
      link: preview.entriesRemoved > 0 ? 'View removed entries' : undefined,
    },
    {
      kind: 'Changed',
      entries: preview.entriesChanged,
      files: preview.filesChanged,
      priority: preview.hashesChanged > 0,
      link: preview.entriesChanged > 0 ? 'View changed entries' : undefined,
    },
    {
      kind: 'Added',
      entries: preview.entriesAdded,
      files: preview.filesAdded,
      priority: false,
      link: undefined,
    },
  ] as const;
  const displayedChanges = preview.activeSha256
    ? changes
    : changes.filter(({ kind }) => kind === 'Added');

  return (
    <Stack
      component="section"
      gap="xs"
      aria-label="DAT changes"
    >
      <Title
        order={3}
        size="h5"
      >
        DAT changes
      </Title>
      <SimpleGrid
        cols={{
          base: 1,
          xs: displayedChanges.length,
        }}
        spacing="xs"
      >
        {displayedChanges.map(({ kind, entries, files, priority, link }) => {
          const entriesShare = percentage(entries, preview.activeEntries);
          const filesShare = percentage(files, preview.activeFiles);
          return (
            <Stack
              key={kind}
              gap={4}
              className={priority ? `${classes.panel} ${classes.priority}` : classes.panel}
            >
              <Text
                fw={600}
                className={priority ? classes.priorityText : undefined}
              >
                {kind}
              </Text>
              <Text size="sm">
                {entries.toLocaleString()} {entries === 1 ? 'DAT entry' : 'DAT entries'}
                {entriesShare && (
                  <Text
                    span
                    size="xs"
                    c="dimmed"
                  >
                    {' '}
                    · {entriesShare}
                  </Text>
                )}
              </Text>
              <Text size="sm">
                {files.toLocaleString()} {files === 1 ? 'expected file' : 'expected files'}
                {filesShare && (
                  <Text
                    span
                    size="xs"
                    c="dimmed"
                  >
                    {' '}
                    · {filesShare}
                  </Text>
                )}
              </Text>
              {kind === 'Changed' && preview.hashesChanged > 0 && (
                <Text
                  size="sm"
                  fw={600}
                  className={classes.priorityText}
                >
                  {preview.hashesChanged.toLocaleString()}{' '}
                  {preview.hashesChanged === 1
                    ? 'file with checksum changes'
                    : 'files with checksum changes'}
                </Text>
              )}
              {link && (
                <Button
                  variant="subtle"
                  size="compact-sm"
                  w="fit-content"
                  px={0}
                  onClick={() => onViewChanges(kind)}
                >
                  {link}
                </Button>
              )}
            </Stack>
          );
        })}
      </SimpleGrid>
      {preview.activeBiosEntries !== preview.candidateBiosEntries && (
        <Text
          size="sm"
          c="dimmed"
        >
          BIOS entries {preview.activeBiosEntries} → {preview.candidateBiosEntries}
        </Text>
      )}
      {(preview.entriesRemoved > 0 || preview.filesRemoved > 0) && (
        <Text
          size="sm"
          c="dimmed"
        >
          Removals refer to DAT definitions. ROMD keeps stored ROM files. Renamed entries appear as
          an addition and a removal.
        </Text>
      )}
    </Stack>
  );
}

function message(error: unknown): string {
  if (
    typeof error === 'object' &&
    error !== null &&
    'detail' in error &&
    typeof error.detail === 'string'
  )
    return error.detail;
  return 'The server could not confirm this request. Check Jobs before retrying an application.';
}

export function DatReplacementReviewModal({
  dat,
  subscription,
  file,
  onClose,
  onAccepted,
}: (
  | {
      dat: Pick<Dat, 'id' | 'name'>;
      subscription?: never;
      file: File;
    }
  | {
      subscription: DatCatalogSubscription;
      dat?: never;
      file?: never;
    }
) & {
  onClose: () => void;
  onAccepted: (result: JobReference) => void;
}) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const datId = dat?.id ?? '';
  const initial = subscription != null && subscription.activeDatId == null;
  const [preview, setPreview] = useState<DatReplacementPreview>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [applying, setApplying] = useState(false);
  const [attempt, setAttempt] = useState(0);
  // biome-ignore lint/correctness/useExhaustiveDependencies: attempt intentionally triggers an explicit user retry, including identical requests.
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setPreview(undefined);
    setError(undefined);
    void (async () => {
      try {
        const response = subscription
          ? await getDatSubscriptionPreview({
              path: {
                subscriptionId: subscription.id,
              },
              signal: controller.signal,
            })
          : await previewDatReplacement({
              path: {
                datId,
              },
              body: {
                file,
              },
              signal: controller.signal,
            });
        if (controller.signal.aborted) return;
        if (response.error || !response.data) setError(message(response.error));
        else setPreview(response.data);
      } catch {
        if (!controller.signal.aborted)
          setError('Unable to reach ROMD. Try again when the server is available.');
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    })();
    return () => controller.abort();
  }, [
    datId,
    subscription,
    file,
    attempt,
  ]);

  const apply = async () => {
    if (!preview || preview.unchanged || applying) return;
    setApplying(true);
    setError(undefined);
    try {
      const response = subscription
        ? await applyDatSubscription({
            path: {
              subscriptionId: subscription.id,
            },
            body: {
              activeSha256: preview.activeSha256,
              candidateSha256: preview.candidateSha256,
            },
          })
        : await createDatReplacement({
            path: {
              datId,
            },
            body: {
              file,
              activeSha256: preview.activeSha256,
              candidateSha256: preview.candidateSha256,
            },
          });
      if (response.error || !response.data) {
        setError(message(response.error));
        setPreview(undefined);
      } else onAccepted(response.data);
    } catch {
      setError('The server did not confirm the request. Check Jobs before trying again.');
      setPreview(undefined);
    } finally {
      setApplying(false);
    }
  };

  return (
    <Modal
      opened
      onClose={onClose}
      title={initial ? 'Review first catalog import' : 'Review catalog update'}
      size="xl"
      fullScreen={mobile}
      classNames={{
        content: classes.dialog,
        body: classes.body,
      }}
      closeOnClickOutside={!applying}
      closeOnEscape={!applying}
      withCloseButton={!applying}
    >
      <Stack className={classes.content}>
        <div>
          <Text
            fw={600}
            className={classes.fieldValue}
          >
            {subscription?.name ?? dat?.name}
          </Text>
          <Text
            size="sm"
            c="dimmed"
            className={classes.fieldValue}
          >
            {file?.name ?? 'Verified subscription catalog'}
          </Text>
        </div>
        {loading && (
          <Group role="status">
            <Loader size="sm" />
            <Text>Validating and comparing the DAT…</Text>
          </Group>
        )}
        {error && (
          <Alert
            color="red"
            title="Review needs attention"
            role="alert"
          >
            {error}
          </Alert>
        )}
        {preview && (
          <>
            <DatReviewVersions
              preview={preview}
              baseline={initial ? 'first-import' : 'installed'}
            />
            {preview.unchanged ? (
              <Alert
                color="green"
                title="Already up to date"
              >
                The installed DAT matches this document exactly.
              </Alert>
            ) : (
              <>
                {preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged === 0 && (
                  <Alert color="blue">
                    No supported entry changes were detected. Header metadata, formatting, or fields
                    outside this comparison may differ.
                  </Alert>
                )}
                <DatChangeBrowser
                  key={`${preview.activeSha256}:${preview.candidateSha256}`}
                  datId={datId}
                  subscriptionId={subscription?.id}
                  file={file}
                  preview={preview}
                  renderSummary={(onViewChanges) => (
                    <DatReviewSummary
                      preview={preview}
                      onViewChanges={onViewChanges}
                    />
                  )}
                  onStale={(detail) => {
                    setError(detail);
                    setPreview(undefined);
                  }}
                />
                <Text
                  size="sm"
                  c="dimmed"
                >
                  Counts describe this DAT. Library impact is not calculated in this preview.
                  Approval queues catalog processing.
                </Text>
                <Text
                  size="sm"
                  c="dimmed"
                >
                  {file
                    ? 'Closing this review discards the upload preview.'
                    : 'This new DAT is saved in ROMD. You can come back to review it later.'}
                </Text>
              </>
            )}
            <details>
              <summary>Document fingerprints</summary>
              <Stack
                gap="xs"
                mt="xs"
              >
                <Text size="xs">Installed document</Text>
                <Code className={classes.fieldValue}>
                  {preview.activeSha256 || 'No installed document yet'}
                </Code>
                <Text size="xs">New document</Text>
                <Code className={classes.fieldValue}>{preview.candidateSha256}</Code>
              </Stack>
            </details>
          </>
        )}
      </Stack>
      <Group
        justify="flex-end"
        gap="xs"
        className={classes.footer}
      >
        <Button
          variant="default"
          onClick={onClose}
          disabled={applying}
        >
          {preview?.unchanged ? 'Done' : 'Cancel'}
        </Button>
        {error && (
          <Button
            variant="light"
            onClick={() => setAttempt((n) => n + 1)}
            disabled={applying || loading}
          >
            Preview again
          </Button>
        )}
        {preview && !preview.unchanged && (
          <Button
            {...workspaceActionProps}
            onClick={() => void apply()}
            loading={applying}
          >
            {initial ? 'Import reviewed catalog' : 'Apply reviewed update'}
          </Button>
        )}
      </Group>
    </Modal>
  );
}
