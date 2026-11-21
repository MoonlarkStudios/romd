import { Checkbox, Group, Select, Stack, Text } from '@mantine/core';

interface RomImportPolicy {
  trackedOnly: boolean;
  trackMatchedTitles: boolean;
  allowUnidentified: boolean;
}

export function RomImportOptions({
  trackedOnly,
  setTrackedOnly,
  trackMatchedTitles,
  setTrackMatchedTitles,
  allowUnidentified,
  setAllowUnidentified,
  canKeepUnmatched,
  disabled,
}: RomImportPolicy & {
  setTrackedOnly: (value: boolean) => void;
  setTrackMatchedTitles: (value: boolean) => void;
  setAllowUnidentified: (value: boolean) => void;
  canKeepUnmatched: boolean;
  disabled: boolean;
}) {
  return (
    <Stack gap="sm">
      <Select
        label="ROMs to keep"
        value={trackedOnly ? 'tracked' : 'all'}
        onChange={(value) => setTrackedOnly(value === 'tracked')}
        disabled={disabled}
        allowDeselect={false}
        data={[
          {
            value: 'all',
            label: 'All catalog matches',
          },
          {
            value: 'tracked',
            label: 'Tracked titles only',
          },
        ]}
        maw={440}
      />
      {!trackedOnly && (
        <Group
          gap="lg"
          align="flex-start"
        >
          <Checkbox
            label="Track matched titles"
            checked={trackMatchedTitles}
            onChange={(event) => setTrackMatchedTitles(event.currentTarget.checked)}
            disabled={disabled}
          />
          {canKeepUnmatched && (
            <Checkbox
              label="Keep unmatched files"
              checked={allowUnidentified}
              onChange={(event) => setAllowUnidentified(event.currentTarget.checked)}
              disabled={disabled}
            />
          )}
        </Group>
      )}
      <Text
        size="sm"
        c="dimmed"
      >
        {trackedOnly
          ? 'Keeps ROMs for titles you already track, including matching regions and revisions. DAT catalogs still import.'
          : canKeepUnmatched && allowUnidentified
            ? 'Files without a catalog match go to Needs attention.'
            : 'Files without a catalog match are skipped.'}
      </Text>
    </Stack>
  );
}
