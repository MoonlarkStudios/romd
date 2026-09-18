import {
  Accordion,
  Alert,
  Button,
  Divider,
  Group,
  MultiSelect,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { LibraryConfigurationDto, LibraryDto, RatingBoard } from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useDats } from '../../hooks/api/useDats';
import { useCatalogFilterFacets, useUpdateLibrary } from '../../hooks/api/useLibraryManagement';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import classes from './Libraries.module.css';
import { TitleOverridePicker, toConfiguration, toFormState } from './libraryForm';
import {
  type MaxMinimumAgeValue,
  maxMinimumAgeOptions,
  type UnknownMetadataPolicyValue,
  unknownMetadataPolicyOptions,
} from './libraryOptions';

export function LibraryRules({
  library,
  section,
  onDirtyChange,
  onConfigurationChange,
}: {
  library: LibraryDto;
  onDirtyChange?: (dirty: boolean) => void;
  onConfigurationChange?: (configuration: LibraryConfigurationDto) => void;
  section: 'games' | 'settings';
}) {
  const [form, setForm] = useState(() => toFormState(library));
  const [saved, setSaved] = useState(() => JSON.stringify(toFormState(library)));
  const update = useUpdateLibrary();
  const platforms = usePlatforms();
  const dats = useDats();
  const facets = useCatalogFilterFacets();
  const dirty = saved !== JSON.stringify(form);
  useEffect(() => {
    onConfigurationChange?.(toConfiguration(form));
  }, [
    form,
    onConfigurationChange,
  ]);
  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [
    dirty,
    onDirtyChange,
  ]);
  useEffect(
    () => () => onDirtyChange?.(false),
    [
      onDirtyChange,
    ],
  );
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) =>
    setForm((current) => ({
      ...current,
      [key]: value,
    }));
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        try {
          await update.mutateAsync({
            libraryId: library.id,
            request: {
              name: section === 'settings' ? form.name.trim() : library.name,
              configuration:
                section === 'games' || !library.configuration
                  ? toConfiguration(form)
                  : {
                      ...library.configuration,
                    },
              isDefault: section === 'settings' ? form.isDefault : library.isDefault,
            },
          });
          setSaved(JSON.stringify(form));
          notifications.show({
            title: 'Library saved',
            message: 'Your audience’s games are updating.',
            color: 'teal',
          });
        } catch {
          /* Mutation error is displayed beside the save action. */
        }
      }}
    >
      <Stack
        gap="xl"
        className={classes.panel}
      >
        {library.configuration === null && (
          <Alert color="yellow">
            This library’s saved rules need repair. Saving replaces them with the rules shown here.
          </Alert>
        )}
        {section === 'games' && (
          <>
            <div>
              <Title
                order={2}
                className={classes.sectionHeading}
              >
                Content ratings
              </Title>
              <Text
                c="dimmed"
                mt="xs"
              >
                Start with the ratings appropriate for this audience. These limits apply to all
                games, including handpicked titles and shared collections.
              </Text>
            </div>
            <Select
              label="Maximum content-rating age"
              description="The age assigned by rating boards, including ESRB. By default, the strictest available board rating is used."
              data={maxMinimumAgeOptions}
              value={form.maxMinimumAge}
              allowDeselect={false}
              onChange={(v) => {
                if (v) set('maxMinimumAge', v as MaxMinimumAgeValue);
              }}
            />
            <Text
              size="sm"
              c="dimmed"
            >
              ESRB age equivalents: Everyone (0), Everyone 10+ (10), Teen (13), Mature (17), Adults
              Only (18). A ceiling includes lower rated ages too; other boards can affect the
              result.
            </Text>
            <Select
              label="When a game has no rating"
              description="Games held for review stay hidden until their rating or your policy changes."
              data={unknownMetadataPolicyOptions}
              value={form.unknownRatingPolicy}
              allowDeselect={false}
              onChange={(v) => {
                if (v) set('unknownRatingPolicy', v as UnknownMetadataPolicyValue);
              }}
            />
            <Accordion
              variant="separated"
              radius="md"
            >
              <Accordion.Item value="ratings">
                <Accordion.Control>Rating sources and exceptions</Accordion.Control>
                <Accordion.Panel>
                  <Stack>
                    <Select
                      label="How ratings are combined"
                      data={[
                        {
                          value: 'Strictest',
                          label: 'Use the strictest rating',
                        },
                        {
                          value: 'Preferred',
                          label: 'Use the first available preferred board',
                        },
                      ]}
                      value={form.contentRatingBasisSelection}
                      allowDeselect={false}
                      onChange={(v) =>
                        set(
                          'contentRatingBasisSelection',
                          v === 'Preferred' ? 'Preferred' : 'Strictest',
                        )
                      }
                    />
                    {form.contentRatingBasisSelection === 'Preferred' && (
                      <MultiSelect
                        label="Rating board preference"
                        description="Ordered by selection. Remove and select a board again to move it to the end."
                        data={[
                          {
                            value: 'Esrb',
                            label: 'ESRB · North America',
                          },
                          {
                            value: 'Pegi',
                            label: 'PEGI · Europe',
                          },
                          {
                            value: 'Cero',
                            label: 'CERO · Japan',
                          },
                          {
                            value: 'Usk',
                            label: 'USK · Germany',
                          },
                          {
                            value: 'Grac',
                            label: 'GRAC · South Korea',
                          },
                          {
                            value: 'ClassInd',
                            label: 'ClassInd · Brazil',
                          },
                          {
                            value: 'Acb',
                            label: 'ACB · Australia',
                          },
                        ]}
                        value={form.contentRatingBoardPreference}
                        onChange={(v) => set('contentRatingBoardPreference', v as RatingBoard[])}
                      />
                    )}
                    <Text
                      size="sm"
                      c="dimmed"
                    >
                      {form.contentRatingBasisSelection === 'Strictest'
                        ? 'Uses the highest age across available rating boards. Board preference does not affect this mode.'
                        : 'Uses the first available rating in your board order. Games without a usable rating follow your unrated-game policy.'}
                    </Text>
                    <Switch
                      label="Allow refused classifications"
                      checked={form.allowRefusedClassification}
                      onChange={(e) => set('allowRefusedClassification', e.currentTarget.checked)}
                    />
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            </Accordion>
          </>
        )}
        {section === 'games' && (
          <>
            <div>
              <Title
                order={2}
                className={classes.sectionHeading}
              >
                Choose their games
              </Title>
              <Text
                c="dimmed"
                mt="xs"
              >
                Build a growing library with rules, or choose every game yourself.
              </Text>
            </div>
            <SegmentedControl
              fullWidth
              color="teal"
              value={form.titleSelectionMode}
              onChange={(value) =>
                set('titleSelectionMode', value === 'IncludeOnly' ? 'IncludeOnly' : 'Rules')
              }
              data={[
                {
                  value: 'Rules',
                  label: 'Select with rules',
                },
                {
                  value: 'IncludeOnly',
                  label: 'Handpick games',
                },
              ]}
            />
            {form.titleSelectionMode === 'Rules' ? (
              <>
                <MultiSelect
                  label="Systems"
                  description="Leave empty to include every system."
                  placeholder="All systems"
                  searchable
                  data={(platforms.data ?? []).map((p) => ({
                    value: p.key,
                    label: p.name,
                  }))}
                  value={form.allowedSystemKeys}
                  onChange={(value) => set('allowedSystemKeys', value)}
                />
                <MultiSelect
                  label="Genres"
                  description="Leave empty to include every genre."
                  placeholder="All genres"
                  searchable
                  data={(facets.data?.genres ?? []).map((g) => ({
                    value: g.value,
                    label: g.value,
                  }))}
                  value={form.allowedGenres}
                  onChange={(value) => set('allowedGenres', value)}
                />
                <Select
                  label="When a genre is unknown"
                  data={unknownMetadataPolicyOptions}
                  value={form.unknownGenrePolicy}
                  allowDeselect={false}
                  onChange={(value) => {
                    if (value) set('unknownGenrePolicy', value as UnknownMetadataPolicyValue);
                  }}
                />
                <Divider />
                <TitleOverridePicker
                  label="Include specific games"
                  description="Bypass genre rules for these games. Content-rating limits and system scope still apply."
                  emptyMessage="No additional games selected."
                  value={form.includeTitleIds}
                  onChange={(value) => set('includeTitleIds', value)}
                />
                <TitleOverridePicker
                  label="Exclude specific games"
                  description="Keep these titles out of this audience’s library."
                  emptyMessage="No games excluded."
                  value={form.excludeTitleIds}
                  onChange={(value) => set('excludeTitleIds', value)}
                />
              </>
            ) : (
              <TitleOverridePicker
                label="Handpicked games"
                description="Only these games are included. Content-rating limits still apply."
                emptyMessage="Choose a game to begin. An empty selection creates an empty library."
                value={form.includeTitleIds}
                onChange={(value) => set('includeTitleIds', value)}
              />
            )}
            <Switch
              color="teal"
              label="Include missing games"
              description="Allow games you do not own to appear where the client supports missing games. Collection shelves show owned games."
              checked={form.showMissingGames}
              onChange={(e) => set('showMissingGames', e.currentTarget.checked)}
            />
            <Accordion
              variant="separated"
              radius="md"
            >
              <Accordion.Item value="sources">
                <Accordion.Control>Advanced source exclusions</Accordion.Control>
                <Accordion.Panel>
                  <MultiSelect
                    label="Excluded DAT files"
                    description="Releases available only from these sources are excluded."
                    searchable
                    data={(dats.data ?? []).map((d) => ({
                      value: d.id,
                      label: d.name,
                    }))}
                    value={form.excludedDatIds}
                    onChange={(value) => set('excludedDatIds', value)}
                  />
                </Accordion.Panel>
              </Accordion.Item>
            </Accordion>
          </>
        )}
        {section === 'settings' && (
          <>
            <Title
              order={2}
              className={classes.sectionHeading}
            >
              Library details
            </Title>
            <TextInput
              label="Library name"
              required
              maxLength={200}
              value={form.name}
              onChange={(e) => set('name', e.currentTarget.value)}
            />
            <Switch
              label="Default library"
              description="Use this audience when assigning the default library to people. Existing assignments do not change."
              checked={form.isDefault}
              onChange={(e) => set('isDefault', e.currentTarget.checked)}
            />
          </>
        )}
        {update.isError && <Alert color="red">{update.error.message}</Alert>}
        <Group
          justify="space-between"
          className={classes.saveBar}
        >
          <Text
            size="sm"
            c="dimmed"
          >
            {dirty ? 'You have unsaved changes.' : 'All changes saved.'}
          </Text>
          <Group>
            <Button
              variant="subtle"
              disabled={!dirty || update.isPending}
              onClick={() => {
                setForm(toFormState(library));
                setSaved(JSON.stringify(toFormState(library)));
              }}
            >
              Reset
            </Button>
            <Button
              {...workspaceActionProps}
              type="submit"
              disabled={!dirty || !form.name.trim()}
              loading={update.isPending}
            >
              Save changes
            </Button>
          </Group>
        </Group>
      </Stack>
    </form>
  );
}
