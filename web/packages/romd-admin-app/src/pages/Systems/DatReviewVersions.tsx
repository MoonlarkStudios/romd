import { SimpleGrid, Text } from '@mantine/core';
import type { DatReplacementPreview } from '@romd/admin-api-client';
import classes from './DatReview.module.css';

export function DatReviewVersions({
  preview,
  baseline,
}: {
  preview: DatReplacementPreview;
  baseline?: 'installed' | 'first-import';
}) {
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
      label: 'New DAT',
      version: preview.candidateVersion,
      entries: preview.candidateEntries,
    },
  ];
  return (
    <SimpleGrid
      component="dl"
      className={classes.versionGrid}
      aria-label="DAT versions"
      cols={baseline ? 2 : 1}
      spacing="xs"
    >
      {versions.map(({ label, version, entries }) => (
        <div
          className={classes.panel}
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
          {entries !== undefined && (
            <Text
              component="dd"
              className={classes.versionValue}
              size="xs"
              c="dimmed"
            >
              {entries.toLocaleString()} DAT {entries === 1 ? 'entry' : 'entries'}
            </Text>
          )}
        </div>
      ))}
    </SimpleGrid>
  );
}
