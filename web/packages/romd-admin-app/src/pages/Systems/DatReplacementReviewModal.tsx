import { Alert, Button, Code, Group, Loader, Modal, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type {
  Dat,
  DatCatalogSubscriptionDto,
  DatReplacementPreview,
  UploadAccepted,
} from '@romd/admin-api-client';
import {
  applyCatalogSubscription,
  applyDatSubscription,
  applyReviewedDatReplacement,
  previewCatalogSubscription,
  previewDatReplacement,
  previewDatSubscription,
} from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { DatChangeBrowser } from './DatChangeBrowser';

function percentage(count: number, baseline: number): string {
  if (baseline === 0) return 'No existing baseline';
  const percent = (100 * count) / baseline;
  return `${count > 0 && percent < 0.1 ? '<0.1' : percent.toFixed(1)}% of current`;
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
      file?: File;
    }
  | {
      subscription: DatCatalogSubscriptionDto;
      dat?: never;
      file?: never;
    }
) & {
  onClose: () => void;
  onAccepted: (result: UploadAccepted) => void;
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
          ? await previewCatalogSubscription({
              path: {
                subscriptionId: subscription.id,
              },
              signal: controller.signal,
            })
          : file
            ? await previewDatReplacement({
                path: {
                  datId,
                },
                body: {
                  file,
                },
                signal: controller.signal,
              })
            : await previewDatSubscription({
                path: {
                  datId,
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
        ? await applyCatalogSubscription({
            path: {
              subscriptionId: subscription.id,
            },
            body: {
              activeSha256: preview.activeSha256,
              candidateSha256: preview.candidateSha256,
            },
          })
        : file
          ? await applyReviewedDatReplacement({
              path: {
                datId,
              },
              body: {
                file,
                activeSha256: preview.activeSha256,
                candidateSha256: preview.candidateSha256,
              },
            })
          : await applyDatSubscription({
              path: {
                datId,
              },
              body: {
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
      closeOnClickOutside={!applying}
      closeOnEscape={!applying}
      withCloseButton={!applying}
    >
      <Stack>
        <div>
          <Text fw={600}>{subscription?.name ?? dat?.name}</Text>
          <Text
            size="sm"
            c="dimmed"
          >
            {file?.name ?? 'Verified subscription catalog'}
          </Text>
        </div>
        <Text size="sm">
          {initial
            ? 'This complete catalog has been downloaded and validated. Nothing is imported until you approve. You can close this window and return to the saved candidate.'
            : file
              ? 'Your current catalog stays active while you review. Closing this window discards this preview.'
              : 'Your current catalog stays active while you review. This downloaded update is saved in ROMD, so you can return to it later.'}
        </Text>
        {loading && (
          <Group>
            <Loader size="sm" />
            <Text>Validating and comparing the complete document…</Text>
          </Group>
        )}
        {error && (
          <Alert
            color="red"
            title="Update needs attention"
          >
            {error}
          </Alert>
        )}
        {preview && (
          <>
            {preview.unchanged ? (
              <Alert
                color="green"
                title="Already up to date"
              >
                This is the exact document already in use. No replacement is needed.
              </Alert>
            ) : (
              <>
                <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
                  <Stack gap={4}><Text size="xs" c="dimmed">Installed DAT</Text><Text fw={600}>{initial ? 'First import' : preview.activeVersion ?? 'Version not specified'}</Text><Text size="sm" c="dimmed">{preview.activeEntries.toLocaleString()} entries</Text></Stack>
                  <Stack gap={4}><Text size="xs" c="dimmed">Candidate DAT</Text><Text fw={600}>{preview.candidateVersion ?? 'Version not specified'}</Text><Text size="sm" c="dimmed">{preview.candidateEntries.toLocaleString()} entries</Text></Stack>
                </SimpleGrid>
                <Table.ScrollContainer minWidth={440}>
                  <Table withTableBorder>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>Changes</Table.Th>
                        <Table.Th>Added</Table.Th>
                        <Table.Th>Removed</Table.Th>
                        <Table.Th>Changed</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      <Table.Tr>
                        <Table.Td>Entries</Table.Td>
                        <Table.Td>
                          {preview.entriesAdded.toLocaleString()}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {percentage(preview.entriesAdded, preview.activeEntries ?? 0)}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          {preview.entriesRemoved.toLocaleString()}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {percentage(preview.entriesRemoved, preview.activeEntries ?? 0)}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          {preview.entriesChanged.toLocaleString()}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {percentage(preview.entriesChanged, preview.activeEntries ?? 0)}
                          </Text>
                        </Table.Td>
                      </Table.Tr>
                      <Table.Tr>
                        <Table.Td>Expected files</Table.Td>
                        <Table.Td>
                          {preview.filesAdded.toLocaleString()}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {percentage(preview.filesAdded, preview.activeFiles ?? 0)}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          {preview.filesRemoved.toLocaleString()}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {percentage(preview.filesRemoved, preview.activeFiles ?? 0)}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          {preview.filesChanged.toLocaleString()}
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {percentage(preview.filesChanged, preview.activeFiles ?? 0)}
                          </Text>
                        </Table.Td>
                      </Table.Tr>
                    </Table.Tbody>
                  </Table>
                </Table.ScrollContainer>
                <Text size="sm">
                  {preview.hashesChanged} existing file checksums changed. BIOS entries:{' '}
                  {preview.activeBiosEntries} → {preview.candidateBiosEntries}.
                </Text>
                {preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged === 0 && (
                  <Alert color="blue">
                    The document changed, but no supported entry changes were detected. Header
                    metadata, formatting, or fields outside this comparison may differ.
                  </Alert>
                )}
                {(preview.entriesRemoved > 0 || preview.hashesChanged > 0) && (
                  <Alert color="yellow">
                    Review removals and changed checksums. Renamed entries appear as an addition and
                    a removal. Applying a DAT update does not delete your stored ROM files.
                  </Alert>
                )}
                <DatChangeBrowser
                  key={`${preview.activeSha256}:${preview.candidateSha256}`}
                  datId={datId}
                  subscriptionId={subscription?.id}
                  file={file}
                  preview={preview}
                  onStale={(detail) => {
                    setError(detail);
                    setPreview(undefined);
                  }}
                />
                <Text
                  size="sm"
                  c="dimmed"
                >
                  These are source catalog changes, not unique title counts. Library impact is not
                  calculated in this preview. Approval starts catalog processing; catalog and
                  library updates follow.
                </Text>
              </>
            )}
            <details>
              <summary>Document fingerprints</summary>
              <Stack
                gap="xs"
                mt="xs"
              >
                <Text size="xs">Active</Text>
                <Code
                  style={{
                    overflowWrap: 'anywhere',
                  }}
                >
                  {preview.activeSha256 || 'No installed document yet'}
                </Code>
                <Text size="xs">Candidate</Text>
                <Code
                  style={{
                    overflowWrap: 'anywhere',
                  }}
                >
                  {preview.candidateSha256}
                </Code>
              </Stack>
            </details>
          </>
        )}
        <Group justify="flex-end">
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
              onClick={() => void apply()}
              loading={applying}
            >
              {initial ? 'Import reviewed catalog' : 'Apply reviewed update'}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
