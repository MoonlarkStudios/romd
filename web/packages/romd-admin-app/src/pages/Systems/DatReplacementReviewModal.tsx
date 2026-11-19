import {
  Alert,
  Badge,
  Button,
  Code,
  Group,
  Loader,
  Modal,
  Stack,
  Text,
  Title,
  Tooltip,
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
import { formatBytes, formatRelativeTime } from '../../utils/format';
import { catalogProviderName } from './catalogProviderName';
import { DatChangeBrowser } from './DatChangeBrowser';
import classes from './DatReview.module.css';
import { DatReviewVersions, formatVersionDate, versionDate } from './DatReviewVersions';
import { decisionConsequence, reviewOutcome } from './datReviewSummary';
import { datProviderLabel } from './sourceCatalog';

/** What the admin approved, kept so the page can report the job against the same facts. */
export type DatReviewOutcome = {
  version: string | null;
  summary: string;
};

type ReviewTarget =
  | {
      dat: Pick<Dat, 'id' | 'name'> & Partial<Pick<Dat, 'type' | 'systemKey'>>;
      subscription?: never;
      file: File;
    }
  | {
      subscription: DatCatalogSubscription;
      dat?: never;
      file?: never;
    };

type ReviewProps = ReviewTarget & {
  systemName?: string;
  provider?: string;
  onClose: () => void;
  onAccepted: (result: JobReference, outcome: DatReviewOutcome) => void;
};

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

function Footnotes({ preview }: { preview: DatReplacementPreview }) {
  return (
    <Stack
      gap={6}
      className={classes.footnotes}
    >
      {preview.activeBiosEntries !== preview.candidateBiosEntries && (
        <Text
          size="xs"
          c="dimmed"
        >
          BIOS entries {preview.activeBiosEntries.toLocaleString()} →{' '}
          {preview.candidateBiosEntries.toLocaleString()}
        </Text>
      )}
      {!!preview.impact?.diskChangedEntries && (
        <Text
          size="xs"
          c="dimmed"
        >
          Stored ROM matches cover this DAT’s ROM files. Disc and CHD matching follow separate rules
          and are not estimated here.
        </Text>
      )}
      {!!preview.impact && (
        <Text
          size="xs"
          c="dimmed"
        >
          Impact uses the stored ROMs and title links present when this review loaded. Stored ROM
          files are never deleted.
        </Text>
      )}
      <Fingerprints preview={preview} />
    </Stack>
  );
}

/**
 * Reviews an installed DAT against an incoming candidate and approves the exact reviewed pair.
 * `layout` decides only the frame: a dialog in setup flows, or a section of the source page.
 */
export function DatReplacementReview({
  dat,
  subscription,
  file,
  systemName,
  provider,
  onClose,
  onAccepted,
  onApplyingChange,
  layout,
}: ReviewProps & {
  layout: 'modal' | 'page';
  onApplyingChange?: (applying: boolean) => void;
}) {
  const mobile = useMediaQuery('(max-width: 48em)') ?? false;
  const datId = dat?.id ?? '';
  const initial = subscription != null && subscription.activeDatId == null;
  const [preview, setPreview] = useState<DatReplacementPreview>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [applying, setApplying] = useState(false);
  const [attempt, setAttempt] = useState(0);
  // biome-ignore lint/correctness/useExhaustiveDependencies: attempt triggers an explicit retry; a polled subscription object must not discard the review unless its id changes.
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
    subscription?.id,
    file,
    attempt,
  ]);

  const apply = async () => {
    if (!preview || preview.unchanged || applying) return;
    setApplying(true);
    onApplyingChange?.(true);
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
      } else
        onAccepted(response.data, {
          version: preview.candidateVersion,
          summary: reviewOutcome(preview),
        });
    } catch {
      setError('The server did not confirm the request. Check Jobs before trying again.');
      setPreview(undefined);
    } finally {
      setApplying(false);
      onApplyingChange?.(false);
    }
  };

  const sourceName = subscription?.name ?? dat?.name;
  const system = systemName ?? dat?.systemKey ?? subscription?.systemKey ?? undefined;
  const providerKey = subscription?.catalogId.split('/')[0];
  const providerLabel =
    provider ??
    (providerKey
      ? catalogProviderName(providerKey)
      : dat?.type
        ? datProviderLabel(dat.type)
        : undefined);
  const checked = subscription?.lastCheckedAt;
  // On the source page the hero already names the source, so the review names only its origin.
  const identity = layout === 'modal';
  const heading = identity ? (system ?? sourceName) : undefined;
  const sourceHeader = (
    <header className={classes.sourceHeader}>
      {heading && (
        <Title
          order={2}
          size="h4"
          className={classes.fieldValue}
        >
          {heading}
        </Title>
      )}
      {identity && sourceName && sourceName !== heading && (
        <Text
          size="sm"
          c="dimmed"
          className={classes.fieldValue}
        >
          {sourceName}
        </Text>
      )}
      <Group
        gap="xs"
        align="center"
        wrap="wrap"
      >
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
    </header>
  );
  const incomingDate = versionDate(preview?.candidateVersion);
  const applyLabel = initial
    ? 'Import catalog'
    : file
      ? 'Apply uploaded DAT'
      : !mobile && incomingDate != null
        ? `Apply ${formatVersionDate(incomingDate, {
            day: 'numeric',
            month: 'short',
          })} update`
        : 'Apply update';
  const closeLabel = preview?.unchanged
    ? 'Done'
    : file
      ? 'Discard upload'
      : initial
        ? 'Not now'
        : 'Decide later';
  const closeHint = preview?.unchanged
    ? undefined
    : file
      ? 'Closing discards this upload. The installed DAT stays active.'
      : 'This update stays saved in ROMD. You can come back to it later.';

  const content = (
    <Stack
      className={layout === 'modal' ? classes.content : classes.pageContent}
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
            header={
              <Stack gap="sm">
                {sourceHeader}
                <DatReviewVersions
                  preview={preview}
                  baseline={initial ? 'first-import' : 'installed'}
                />
              </Stack>
            }
            footnotes={<Footnotes preview={preview} />}
            linkTitles={layout === 'page' && subscription != null}
            onStale={(detail) => {
              setError(detail);
              setPreview(undefined);
            }}
          />
        ))}
    </Stack>
  );
  const footer = (
    <div
      className={layout === 'modal' ? classes.footer : `${classes.footer} ${classes.pageFooter}`}
    >
      {preview && !preview.unchanged && (
        <Text
          size="sm"
          className={classes.footerNote}
        >
          {decisionConsequence(preview)}
        </Text>
      )}
      <Group
        justify="flex-end"
        gap="xs"
        className={classes.decisionActions}
      >
        <Tooltip
          label={closeHint}
          disabled={!closeHint}
          multiline
          w={240}
        >
          <Button
            variant="default"
            onClick={onClose}
            disabled={applying}
          >
            {closeLabel}
          </Button>
        </Tooltip>
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
            {applyLabel}
          </Button>
        )}
      </Group>
    </div>
  );

  if (layout === 'page')
    return (
      <section
        className={classes.pageReview}
        aria-label={initial ? 'Review first catalog import' : 'Review catalog update'}
      >
        {content}
        {footer}
      </section>
    );
  return (
    <>
      {content}
      {footer}
    </>
  );
}

/** The dialog frame, used where the review interrupts a setup flow rather than a source page. */
export function DatReplacementReviewModal(props: ReviewProps) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const initial = props.subscription != null && props.subscription.activeDatId == null;
  const [applying, setApplying] = useState(false);
  return (
    <Modal
      opened
      onClose={props.onClose}
      title={initial ? 'Review first catalog import' : 'Review catalog update'}
      size="min(1280px, 94vw)"
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
      <DatReplacementReview
        {...props}
        layout="modal"
        onApplyingChange={setApplying}
      />
    </Modal>
  );
}
