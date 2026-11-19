import { Text } from '@mantine/core';
import type { DatReplacementPreview } from '@romd/admin-api-client';
import { IconArrowRight } from '@tabler/icons-react';
import classes from './DatReview.module.css';

/** Parses date-stamped DAT versions such as No-Intro's `20261025-205653` or `2026-10-25`. */
export function versionDate(version: string | null | undefined): number | undefined {
  const match = version?.match(/^(\d{4})(-?)(\d{2})\2(\d{2})(?:-\d{6})?$/);
  if (!match) return undefined;
  const year = Number(match[1]);
  const month = Number(match[3]) - 1;
  const day = Number(match[4]);
  const date = new Date(Date.UTC(year, month, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month || date.getUTCDate() !== day)
    return undefined;
  return date.getTime();
}

export function formatVersionDate(
  time: number,
  options: Intl.DateTimeFormatOptions = {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  },
) {
  return new Date(time).toLocaleDateString(undefined, {
    ...options,
    timeZone: 'UTC',
  });
}

function entriesLabel(count: number) {
  return `${count.toLocaleString()} ${count === 1 ? 'entry' : 'entries'}`;
}

function Version({
  label,
  version,
  entries,
  note,
  incoming = false,
}: {
  label: string;
  version: string | null;
  entries?: string;
  note?: string;
  incoming?: boolean;
}) {
  const date = versionDate(version);
  return (
    <dl
      className={incoming ? `${classes.version} ${classes.incomingVersion}` : classes.version}
      aria-label={label}
    >
      <Text
        component="dt"
        size="xs"
        c="dimmed"
      >
        {label}
        {note && ` · ${note}`}
      </Text>
      <Text
        component="dd"
        fw={600}
        className={classes.versionValue}
      >
        {date != null ? formatVersionDate(date) : version || 'Version not specified'}
      </Text>
      {(date != null || entries) && (
        <Text
          component="dd"
          size="xs"
          c="dimmed"
          className={classes.versionValue}
        >
          {date != null && <span className={classes.hash}>{version}</span>}
          {date != null && entries && ' · '}
          {entries}
        </Text>
      )}
    </dl>
  );
}

export function DatReviewVersions({
  preview,
  baseline,
}: {
  preview: DatReplacementPreview;
  baseline?: 'installed' | 'first-import';
}) {
  const delta = preview.candidateEntries - preview.activeEntries;
  const installedDate = versionDate(preview.activeVersion);
  const incomingDate = versionDate(preview.candidateVersion);
  const days =
    baseline === 'installed' && installedDate != null && incomingDate != null
      ? Math.round((incomingDate - installedDate) / 86_400_000)
      : 0;
  const changed = preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged;
  return (
    <section
      className={classes.versionStrip}
      aria-label="DAT versions"
    >
      {baseline && (
        <>
          <Version
            label="Installed DAT"
            version={baseline === 'installed' ? preview.activeVersion : 'No DAT installed'}
            entries={baseline === 'installed' ? entriesLabel(preview.activeEntries) : undefined}
          />
          <IconArrowRight
            size={18}
            className={classes.versionArrow}
            aria-hidden
          />
        </>
      )}
      <Version
        incoming
        label="Incoming DAT"
        note={
          days
            ? `${Math.abs(days).toLocaleString()} ${Math.abs(days) === 1 ? 'day' : 'days'} ${days > 0 ? 'newer' : 'older'}`
            : undefined
        }
        version={preview.candidateVersion}
        entries={`${entriesLabel(preview.candidateEntries)}${
          baseline === 'installed' && delta
            ? ` (${delta > 0 ? '+' : '−'}${Math.abs(delta).toLocaleString()})`
            : ''
        }`}
      />
      {baseline === 'installed' && !preview.unchanged && preview.activeEntries > 0 && (
        <Text
          size="xs"
          c="dimmed"
          className={classes.versionScope}
        >
          {changed.toLocaleString()} {changed === 1 ? 'changed entry' : 'changed entries'}
          {changed > 0 && changed / preview.activeEntries >= 0.01
            ? ` · ${Math.round((100 * changed) / preview.activeEntries)}% of installed`
            : ''}
        </Text>
      )}
    </section>
  );
}
