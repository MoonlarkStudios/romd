import {
  Alert,
  Badge,
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
import { IconChevronRight } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { formatBytes, formatRelativeTime } from '../../utils/format';
import { catalogProviderName } from './catalogProviderName';
import { DatChangeBrowser, type DatChangeFilter } from './DatChangeBrowser';
import classes from './DatReview.module.css';
import { DatReviewVersions } from './DatReviewVersions';
import { datProviderLabel } from './sourceCatalog';

function percentage(count: number, baseline: number | undefined) {
  if (!count || !baseline) return undefined;
  const percent = (100 * count) / baseline;
  return `${percent < 0.1 ? '<0.1' : percent.toFixed(1)}% of current`;
}

function countLabel(count: number, singular: string, plural = `${singular}s`) {
  return `${count.toLocaleString()} ${count === 1 ? singular : plural}`;
}

function DatReviewSummary({
  preview,
  onViewChanges,
}: {
  preview: DatReplacementPreview;
  onViewChanges: (change: DatChangeFilter) => void;
}) {
  const impact = preview.impact;
  const cards: {
    label: string;
    count: number;
    note?: string;
    share?: string;
    filter: DatChangeFilter;
  }[] = [
    {
      label: 'removed',
      count: preview.entriesRemoved,
      note: impact
        ? countLabel(
            impact.removedWithStoredRoms,
            'entry with stored ROMs',
            'entries with stored ROMs',
          )
        : undefined,
      share: percentage(preview.entriesRemoved, preview.activeEntries),
      filter: 'Removed',
    },
    {
      label: impact ? 'SHA-1 changed' : 'Checksums changed',
      count: impact?.sha1ChangedFiles ?? preview.hashesChanged,
      note: impact
        ? countLabel(impact.sha1ChangedEntries, 'affected entry', 'affected entries')
        : undefined,
      share: percentage(impact?.sha1ChangedFiles ?? preview.hashesChanged, preview.activeFiles),
      filter: impact ? 'Sha1Changed' : 'Changed',
    },
  ];
  const secondary: [
    DatChangeFilter,
    number,
    string,
  ][] = [
    [
      'Added',
      preview.entriesAdded,
      'added',
    ],
    [
      'MetadataOnly',
      impact?.metadataOnlyEntries ?? 0,
      'metadata only',
    ],
    [
      'OtherChecksumOnly',
      impact?.otherChecksumOnlyEntries ?? 0,
      'CRC/MD5 only',
    ],
  ];
  const reviewCounts: [
    DatChangeFilter,
    number,
    string,
    string,
  ][] = [
    [
      'LostMatches',
      impact?.lostRomMatches ?? 0,
      'ROM losing its catalog match',
      'ROMs losing their catalog match',
    ],
    [
      'NewMatches',
      impact?.newRomMatches ?? 0,
      'ROM gaining a catalog match',
      'ROMs gaining a catalog match',
    ],
    [
      'RemovedLinked',
      impact?.removedWithTitleLinks ?? 0,
      'removal with a title link',
      'removals with title links',
    ],
  ];
  return (
    <Stack
      component="section"
      gap="sm"
      aria-label="Catalog impact"
    >
      <Group
        justify="space-between"
        align="baseline"
        gap="xs"
      >
        <Title
          order={3}
          size="h5"
        >
          Catalog impact
        </Title>
        {!!impact?.needsReviewEntries && (
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={() => onViewChanges('NeedsReview')}
          >
            {countLabel(impact.needsReviewEntries, 'entry needs review', 'entries need review')}
            <IconChevronRight size={15} />
          </Button>
        )}
      </Group>
      {impact && !!preview.activeSha256 && !impact.needsReviewEntries && (
        <Text
          size="sm"
          c="dimmed"
        >
          No stored ROM matches or title links need review.
        </Text>
      )}
      {cards.some((card) => card.count > 0) && (
        <SimpleGrid
          cols={{
            base: 1,
            xs: 2,
          }}
          spacing="xs"
        >
          {cards
            .filter((card) => card.count > 0)
            .map((card) => (
              <button
                key={card.filter}
                type="button"
                className={`${classes.panel} ${classes.impactCard}`}
                onClick={() => onViewChanges(card.filter)}
              >
                <span>
                  <Group
                    component="span"
                    gap="xs"
                    align="baseline"
                  >
                    <Text
                      component="span"
                      className={classes.priorityText}
                      fw={600}
                    >
                      {card.count.toLocaleString()} {card.label}
                    </Text>
                    {card.share && (
                      <Text
                        component="span"
                        size="xs"
                        c="dimmed"
                      >
                        · {card.share}
                      </Text>
                    )}
                  </Group>
                  {card.note && (
                    <Text
                      component="span"
                      display="block"
                      size="xs"
                      c="dimmed"
                    >
                      {card.note}
                    </Text>
                  )}
                </span>
                <IconChevronRight
                  size={18}
                  aria-hidden
                />
              </button>
            ))}
        </SimpleGrid>
      )}
      <Group
        gap="xs"
        wrap="wrap"
      >
        {reviewCounts
          .filter(([, count]) => count > 0)
          .map(([filter, count, singular, plural]) => (
            <Button
              key={filter}
              size="compact-sm"
              variant="light"
              color={filter === 'NewMatches' ? 'teal' : 'yellow'}
              onClick={() => onViewChanges(filter)}
              rightSection={<IconChevronRight size={14} />}
            >
              {countLabel(count, singular, plural)}
            </Button>
          ))}
        {!!impact?.removedWithStoredRoms && (
          <Button
            size="compact-sm"
            variant="default"
            onClick={() => onViewChanges('RemovedStored')}
          >
            {countLabel(
              impact.removedWithStoredRoms,
              'removal with stored ROMs',
              'removals with stored ROMs',
            )}
          </Button>
        )}
      </Group>
      <Group
        gap="xs"
        wrap="wrap"
      >
        {secondary
          .filter(([, count]) => count > 0)
          .map(([filter, count, label]) => (
            <Button
              key={filter}
              size="compact-sm"
              variant="subtle"
              onClick={() => onViewChanges(filter)}
            >
              {count.toLocaleString()} {label}
              {filter === 'Added' && !!preview.activeEntries && (
                <Text
                  span
                  ml={4}
                  size="xs"
                  c="dimmed"
                >
                  · {percentage(count, preview.activeEntries)}
                </Text>
              )}
            </Button>
          ))}
        <Button
          size="compact-sm"
          variant="default"
          onClick={() => onViewChanges('All')}
        >
          Browse all changes
        </Button>
      </Group>
      {preview.activeBiosEntries !== preview.candidateBiosEntries && (
        <Text
          size="xs"
          c="dimmed"
        >
          BIOS entries {preview.activeBiosEntries.toLocaleString()} →{' '}
          {preview.candidateBiosEntries.toLocaleString()}
        </Text>
      )}
      {!!impact?.diskChangedEntries && (
        <Text
          size="xs"
          c="dimmed"
        >
          The stored ROM estimate covers DAT ROM rows. Disc and CHD matching are outside this
          estimate.
        </Text>
      )}
    </Stack>
  );
}

function ReviewConsequences({ preview }: { preview: DatReplacementPreview }) {
  const impact = preview.impact;
  const losses = impact?.lostRomMatches ?? 0;
  const links = impact?.removedWithTitleLinks ?? 0;
  return (
    <details className={classes.disclosure}>
      <summary>What applying does</summary>
      <Stack
        gap="xs"
        pt="xs"
      >
        <Text size="sm">
          {preview.activeSha256 && preview.activeVersion && preview.candidateVersion
            ? `ROMD queues a job to replace ${preview.activeVersion} with ${preview.candidateVersion}.`
            : preview.activeSha256
              ? 'ROMD queues a job to replace the installed DAT.'
              : 'ROMD queues a job to import this DAT.'}{' '}
          Affected libraries refresh after processing. ROMD keeps all stored ROM files.
        </Text>
        {!!losses && (
          <Text size="sm">
            {countLabel(losses, 'stored ROM will', 'stored ROMs will')} lose a match in this
            catalog. Matches in other catalogs can remain.
          </Text>
        )}
        {!!links && (
          <Text size="sm">
            {countLabel(links, 'removed entry has', 'removed entries have')} a title link that may
            be lost, including any manual match on that entry.
          </Text>
        )}
        {!impact && !!preview.activeSha256 && (
          <Text size="sm">
            Library impact is not calculated in this preview. Stored ROMs that match removed entries
            or old SHA-1 values may lose their match in this catalog.
          </Text>
        )}
        {!!impact && (
          <Text
            size="sm"
            c="dimmed"
          >
            This estimate uses the ROMs and title links present when the review is loaded.
          </Text>
        )}
      </Stack>
    </details>
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
  systemName,
  provider,
  onClose,
  onAccepted,
}: (
  | {
      dat: Pick<Dat, 'id' | 'name'> & Partial<Pick<Dat, 'type' | 'systemKey'>>;
      subscription?: never;
      file: File;
    }
  | {
      subscription: DatCatalogSubscription;
      dat?: never;
      file?: never;
    }
) & {
  systemName?: string;
  provider?: string;
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

  const sourceName = subscription?.name ?? dat?.name;
  const system = systemName ?? dat?.systemKey ?? subscription?.systemKey;
  const providerKey = subscription?.catalogId.split('/')[0];
  const providerLabel =
    provider ??
    (providerKey
      ? catalogProviderName(providerKey)
      : dat?.type
        ? datProviderLabel(dat.type)
        : undefined);
  const checked = subscription?.lastCheckedAt;
  const totalChanges = preview
    ? preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged
    : 0;
  const losses = preview?.impact?.lostRomMatches ?? 0;
  const sourceHeader = (
    <Stack
      gap={6}
      className={classes.sourceHeader}
    >
      <Text
        fw={600}
        className={classes.fieldValue}
      >
        {sourceName}
      </Text>
      <Group
        gap="xs"
        align="center"
        wrap="wrap"
      >
        {system && system !== sourceName && <Text size="sm">{system}</Text>}
        <Badge
          size="sm"
          variant="light"
          color={file ? 'gray' : 'teal'}
        >
          {file ? 'Manual upload' : 'Subscription'}
        </Badge>
        {providerLabel && <Text size="sm">{providerLabel}</Text>}
        {checked && (
          <Text
            size="xs"
            c="dimmed"
            component="time"
            dateTime={checked}
            title={new Date(checked).toLocaleString()}
          >
            Checked {formatRelativeTime(checked)}
          </Text>
        )}
        {file && (
          <Text
            size="xs"
            c="dimmed"
            className={classes.fieldValue}
          >
            {file.name} · {formatBytes(file.size)}
          </Text>
        )}
      </Group>
    </Stack>
  );

  return (
    <Modal
      opened
      onClose={onClose}
      title={initial ? 'Review first catalog import' : 'Review catalog update'}
      size="min(1200px, 92vw)"
      fullScreen={mobile}
      closeButtonProps={{
        'aria-label': 'Close review',
      }}
      classNames={{
        content: classes.dialog,
        body: classes.body,
        title: 'romd-section-heading',
      }}
      closeOnClickOutside={!applying}
      closeOnEscape={!applying}
      withCloseButton={!applying}
    >
      <Stack
        className={classes.content}
        gap="md"
      >
        {(!preview || preview.unchanged) && sourceHeader}
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
        {preview &&
          (preview.unchanged ? (
            <>
              <DatReviewVersions
                preview={preview}
                baseline="installed"
              />
              <Alert
                color="green"
                title="Already up to date"
              >
                The installed DAT matches this document exactly.
              </Alert>
              <Fingerprints preview={preview} />
            </>
          ) : (
            <DatChangeBrowser
              key={`${preview.activeSha256}:${preview.candidateSha256}`}
              datId={datId}
              subscriptionId={subscription?.id}
              file={file}
              preview={preview}
              renderSummary={(onViewChanges) => (
                <Stack gap="md">
                  {sourceHeader}
                  <DatReviewVersions
                    preview={preview}
                    baseline={initial ? 'first-import' : 'installed'}
                  />
                  {totalChanges > 0 ? (
                    <DatReviewSummary
                      preview={preview}
                      onViewChanges={onViewChanges}
                    />
                  ) : (
                    <Alert color="blue">
                      No supported entry changes were detected. Header metadata, formatting, or
                      fields outside this comparison may differ.
                    </Alert>
                  )}
                  <div className={classes.reviewDisclosures}>
                    <ReviewConsequences preview={preview} />
                    <Fingerprints preview={preview} />
                  </div>
                </Stack>
              )}
              onStale={(detail) => {
                setError(detail);
                setPreview(undefined);
              }}
            />
          ))}
      </Stack>
      <div className={classes.footer}>
        {preview && !preview.unchanged && (
          <Text
            size="xs"
            c="dimmed"
            className={classes.footerNote}
          >
            {losses > 0
              ? `${countLabel(losses, 'stored ROM will', 'stored ROMs will')} lose a match in this catalog. `
              : ''}
            {file
              ? 'ROMD keeps your stored ROM files.'
              : 'You can come back to this candidate later. It’s saved in ROMD.'}
          </Text>
        )}
        <Group
          justify="flex-end"
          gap="xs"
          className={classes.decisionActions}
        >
          <Button
            variant="default"
            onClick={onClose}
            disabled={applying}
          >
            {preview?.unchanged
              ? 'Done'
              : file
                ? 'Discard upload'
                : initial
                  ? 'Not now'
                  : 'Decide later'}
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
              {initial ? 'Import catalog' : file ? 'Apply uploaded DAT' : 'Apply update'}
            </Button>
          )}
        </Group>
      </div>
    </Modal>
  );
}

function Fingerprints({ preview }: { preview: DatReplacementPreview }) {
  return (
    <details className={classes.disclosure}>
      <summary>Document fingerprints</summary>
      <Stack
        gap="xs"
        pt="xs"
      >
        <Text size="xs">Installed document</Text>
        <Code className={classes.fieldValue}>
          {preview.activeSha256 || 'No installed document yet'}
        </Code>
        <Text size="xs">Incoming document</Text>
        <Code className={classes.fieldValue}>{preview.candidateSha256}</Code>
      </Stack>
    </details>
  );
}
