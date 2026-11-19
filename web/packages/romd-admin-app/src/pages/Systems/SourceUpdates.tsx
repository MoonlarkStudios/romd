import { Alert, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import type { Dat, DatCatalogSubscription, JobReference } from '@romd/admin-api-client';
import {
  IconAlertTriangle,
  IconCircleCheck,
  IconClockPause,
  IconLoader2,
  IconRefresh,
} from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import { formatRelativeTime } from '../../utils/format';
import { DatUploadZone } from './components/DatUploadZone';
import type { ReviewViewChange } from './DatChangeBrowser';
import { DatReplacementReview, type DatReviewOutcome } from './DatReplacementReviewModal';
import classes from './DatReview.module.css';
import { type ReviewView, reviewViewFromParams, withReviewView } from './datReviewSummary';
import {
  subscriptionReady,
  useSubscriptionCheck,
  useSubscriptionStatus,
} from './SourceSubscriptionActions';

type Accepted = (result: JobReference, outcome?: DatReviewOutcome) => void;

/** History state marking a review step the reader pushed, so in-page Back can pop it. */
type ReviewStepState = {
  reviewStep?: boolean;
};

function withoutReviewStep(state: unknown) {
  if (!state || typeof state !== 'object') return state;
  const { reviewStep: _, ...rest } = state as ReviewStepState & Record<string, unknown>;
  return Object.keys(rest).length ? rest : null;
}

/**
 * Keeps the reader's place in the review in the URL (`filter`, `entry`, `q`) so reload, Back from
 * a title, and shared links return to the same entry. Narrow-screen steps push history entries;
 * wide-screen selections replace the current one.
 */
function useReviewLocation() {
  const [params, setParams] = useSearchParams();
  const { state } = useLocation();
  const navigate = useNavigate();
  const view = reviewViewFromParams(params);
  const onViewChange = (next: ReviewView, change: ReviewViewChange) => {
    if (change === 'back' && (state as ReviewStepState | null)?.reviewStep) {
      void navigate(-1);
      return;
    }
    setParams(withReviewView(params, next), {
      replace: change !== 'push',
      state:
        change === 'push'
          ? {
              ...((state as object | null) ?? {}),
              reviewStep: true,
            }
          : state,
    });
  };
  const clear = () =>
    setParams(withReviewView(params, {}), {
      replace: true,
      state: withoutReviewStep(state),
    });
  return {
    view,
    onViewChange,
    clear,
  };
}

function StatusPanel({
  icon,
  tone = 'neutral',
  title,
  children,
  actions,
}: {
  icon: ReactNode;
  tone?: 'neutral' | 'clear' | 'review';
  title: string;
  children: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <section
      className={classes.updateStatus}
      data-tone={tone}
      aria-label="Update status"
    >
      <span
        className={classes.updateStatusIcon}
        aria-hidden
      >
        {icon}
      </span>
      <div className={classes.updateStatusBody}>
        <Title
          order={2}
          size="h4"
        >
          {title}
        </Title>
        <div className={classes.updateStatusDetail}>{children}</div>
      </div>
      {actions && <Group gap="xs">{actions}</Group>}
    </section>
  );
}

function when(time: string | null | undefined) {
  return time ? (
    <time
      dateTime={time}
      title={new Date(time).toLocaleString()}
    >
      {formatRelativeTime(time)}
    </time>
  ) : null;
}

/**
 * A subscribed source's Updates tab is either the pending update's review or the reason there is
 * none, with a way to ask the publisher again.
 */
function SubscriptionUpdates({
  subscription,
  systemName,
  provider,
  jobNotice,
  onAccepted,
}: {
  subscription: DatCatalogSubscription;
  systemName?: string;
  provider: string;
  jobNotice: boolean;
  onAccepted: Accepted;
}) {
  const client = useQueryClient();
  const { status, current } = useSubscriptionStatus(subscription);
  const { check, checking, error } = useSubscriptionCheck(current.id);
  const review = useReviewLocation();
  const ready = subscriptionReady(current);
  const failed = current.state === 'CheckFailed';
  const applying = current.state === 'Applying';
  const paused = !!current.automaticChecksPaused;
  const checkButton = (label: string, variant: 'default' | 'subtle' = 'default') => (
    <Button
      variant={variant}
      size={variant === 'subtle' ? 'compact-sm' : 'sm'}
      leftSection={<IconRefresh size={15} />}
      loading={checking}
      onClick={() => void check()}
    >
      {label}
    </Button>
  );
  const alerts = (
    <>
      {error && (
        <Alert
          color="yellow"
          role="alert"
        >
          {error}
        </Alert>
      )}
      {status.isError && (
        <Alert color="yellow">
          Update status could not be loaded.
          <Button
            variant="subtle"
            onClick={() => void status.refetch()}
          >
            Retry status
          </Button>
        </Alert>
      )}
    </>
  );

  if (ready && !checking)
    return (
      <Stack gap="sm">
        {alerts}
        <DatReplacementReview
          key={`${current.id}:${current.candidateSha256 ?? ''}`}
          layout="page"
          subscription={current}
          systemName={systemName}
          provider={provider}
          headerActions={paused ? undefined : checkButton('Check again', 'subtle')}
          view={review.view}
          onViewChange={review.onViewChange}
          onAccepted={(result, outcome) => {
            client.setQueryData(
              [
                'source-subscription',
                current.id,
              ],
              {
                ...current,
                state: 'Applying',
                jobId: result.jobId,
              },
            );
            review.clear();
            onAccepted(result, outcome);
          }}
        />
      </Stack>
    );

  const checked = when(current.lastCheckedAt);
  // Once a check is due the scheduler owns when it actually starts.
  const next =
    current.nextCheckAt && Date.parse(current.nextCheckAt) > Date.now() ? (
      <time dateTime={current.nextCheckAt}>
        {new Date(current.nextCheckAt).toLocaleString(undefined, {
          dateStyle: 'medium',
          timeStyle: 'short',
        })}
      </time>
    ) : null;
  let panel: ReactNode;
  if (checking)
    panel = (
      <StatusPanel
        icon={
          <IconLoader2
            size={22}
            className={classes.spin}
          />
        }
        title="Checking for updates"
      >
        <Text size="sm">Asking {provider} for a newer DAT. The installed DAT stays active.</Text>
      </StatusPanel>
    );
  else if (applying)
    panel = jobNotice ? null : (
      <StatusPanel
        icon={
          <IconLoader2
            size={22}
            className={classes.spin}
          />
        }
        title="Applying update"
        actions={
          current.jobId && (
            <Button
              component={Link}
              to={`/jobs/${current.jobId}`}
              variant="default"
            >
              View progress
            </Button>
          )
        }
      >
        <Text size="sm">Automatic checks resume after processing finishes.</Text>
      </StatusPanel>
    );
  else if (paused)
    panel = (
      <StatusPanel
        icon={<IconClockPause size={22} />}
        title="Automatic checks paused"
      >
        <Text size="sm">
          Restore the system or enable its source and server subscriptions to resume checks.
        </Text>
        {checked && (
          <Text
            size="sm"
            c="dimmed"
          >
            Last checked {checked}.
          </Text>
        )}
      </StatusPanel>
    );
  else if (failed)
    panel = (
      <StatusPanel
        icon={<IconAlertTriangle size={22} />}
        tone="review"
        title="Update check failed"
        actions={
          <>
            {current.jobId && (
              <Button
                component={Link}
                to={`/jobs/${current.jobId}`}
                variant="subtle"
              >
                View last job
              </Button>
            )}
            {checkButton('Retry check')}
          </>
        }
      >
        {current.message && <Text size="sm">{current.message}</Text>}
        <Text
          size="sm"
          c="dimmed"
        >
          {current.activeDatId ? 'Your installed DAT is unchanged.' : 'Nothing is installed yet.'}
          {next && <> Automatic retry {next}.</>}
        </Text>
      </StatusPanel>
    );
  else if (current.state === 'NotChecked')
    panel = (
      <StatusPanel
        icon={<IconRefresh size={22} />}
        title="Not checked yet"
        actions={checkButton(current.activeDatId ? 'Check for updates' : 'Check for first DAT')}
      >
        <Text size="sm">ROMD checks {provider} daily. Every update waits for your review.</Text>
      </StatusPanel>
    );
  else
    panel = (
      <StatusPanel
        icon={<IconCircleCheck size={22} />}
        tone="clear"
        title="No new updates"
        actions={checkButton('Check for updates')}
      >
        <Text size="sm">The installed DAT is the newest {provider} release.</Text>
        {(checked || next) && (
          <Text
            size="sm"
            c="dimmed"
          >
            {checked && <>Checked {checked}.</>}
            {checked && next && ' '}
            {next && <>Next check {next}.</>} Every update waits for your review.
          </Text>
        )}
      </StatusPanel>
    );
  return (
    <Stack gap="sm">
      {alerts}
      {panel}
    </Stack>
  );
}

export function SourceUpdates({
  dat,
  systemName,
  provider,
  isAdmin,
  subscribed,
  subscription,
  subscriptionStatus,
  canUpload,
  file,
  onFile,
  uploading,
  jobNotice,
  offer,
  onAccepted,
}: {
  dat: Dat;
  systemName?: string;
  provider: string;
  isAdmin: boolean;
  subscribed: boolean;
  subscription?: DatCatalogSubscription;
  subscriptionStatus: {
    pending: boolean;
    retry: () => void;
  };
  canUpload: boolean;
  /** An uploaded DAT lives only in page memory, so its review is not in the URL. */
  file?: File;
  onFile: (file?: File) => void;
  uploading: boolean;
  /** True while the page already shows the job this tab started. */
  jobNotice: boolean;
  /** How to subscribe instead, when the publisher's directory lists this source. */
  offer?: ReactNode;
  onAccepted: Accepted;
}) {
  if (subscribed && !isAdmin)
    return (
      <StatusPanel
        icon={<IconRefresh size={22} />}
        title="Subscription updates"
      >
        <Text size="sm">An administrator reviews and applies updates for this source.</Text>
      </StatusPanel>
    );
  if (subscribed && !subscription)
    return subscriptionStatus.pending ? (
      <Skeleton height={96} />
    ) : (
      <Alert color="yellow">
        Subscription status could not be loaded.
        <Button
          variant="subtle"
          onClick={subscriptionStatus.retry}
        >
          Retry status
        </Button>
      </Alert>
    );
  if (subscription)
    return (
      <SubscriptionUpdates
        key={subscription.id}
        subscription={subscription}
        systemName={systemName}
        provider={provider}
        jobNotice={jobNotice}
        onAccepted={onAccepted}
      />
    );
  if (file && canUpload)
    return (
      <DatReplacementReview
        key={`${dat.id}:${file.name}:${file.lastModified}`}
        layout="page"
        dat={dat}
        file={file}
        systemName={systemName}
        onClose={() => onFile(undefined)}
        onAccepted={(result, outcome) => {
          onFile(undefined);
          onAccepted(result, outcome);
        }}
      />
    );
  return (
    <Stack gap="md">
      <StatusPanel
        icon={<IconRefresh size={22} />}
        title="Manual updates"
      >
        <Text size="sm">
          Upload a newer {provider} DAT to review it. The installed DAT stays active until you
          approve the change.
        </Text>
      </StatusPanel>
      {canUpload && (
        <DatUploadZone
          loading={uploading}
          onDrop={(files) => onFile(files[0])}
        />
      )}
      {offer}
    </Stack>
  );
}
