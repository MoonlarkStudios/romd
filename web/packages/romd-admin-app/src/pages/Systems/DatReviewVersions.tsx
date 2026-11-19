import { Text } from '@mantine/core';
import type { DatReplacementPreview } from '@romd/admin-api-client';
import { IconArrowRight } from '@tabler/icons-react';
import classes from './DatReview.module.css';

function versionDate(version: string | null): number | undefined {
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
  const daysApart =
    installedDate != null && incomingDate != null ? (incomingDate - installedDate) / 86_400_000 : 0;
  const versions = [
    ...(baseline
      ? [
          {
            label: 'Installed DAT',
            version: baseline === 'installed' ? preview.activeVersion : 'No DAT installed',
            entries: baseline === 'installed' ? preview.activeEntries : undefined,
          },
        ]
      : []),
    {
      label: 'Incoming DAT',
      version: preview.candidateVersion,
      entries: preview.candidateEntries,
      incoming: true,
    },
  ];
  return (
    <section
      className={classes.versionGrid}
      data-comparison={baseline ? true : undefined}
      aria-label="DAT versions"
    >
      {versions.map(({ label, version, entries, incoming }) => (
        <dl
          className={`${classes.panel} ${incoming ? classes.incomingVersion : ''}`}
          key={label}
        >
          <Text
            component="dt"
            size="xs"
            c="dimmed"
          >
            {label}
          </Text>
          <Text
            component="dd"
            fw={600}
            className={classes.versionValue}
          >
            {version || 'Version not specified'}
          </Text>
          {incoming && baseline === 'installed' && daysApart !== 0 && (
            <Text
              component="dd"
              className={classes.versionValue}
              size="xs"
              c="dimmed"
            >
              {Math.abs(daysApart).toLocaleString()} {Math.abs(daysApart) === 1 ? 'day' : 'days'}{' '}
              {daysApart > 0 ? 'newer' : 'earlier'}
            </Text>
          )}
          {entries !== undefined && (
            <Text
              component="dd"
              className={classes.versionValue}
              size="xs"
              c="dimmed"
            >
              {entries.toLocaleString()} DAT {entries === 1 ? 'entry' : 'entries'}
              {incoming && baseline === 'installed' && delta !== 0 && (
                <Text
                  span
                  inherit
                  fw={600}
                >
                  {' '}
                  ({delta > 0 ? '+' : '−'}
                  {Math.abs(delta).toLocaleString()})
                </Text>
              )}
            </Text>
          )}
        </dl>
      ))}
      {baseline && (
        <IconArrowRight
          size={20}
          className={classes.versionArrow}
          aria-hidden
        />
      )}
    </section>
  );
}
