import {
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  MultiSelect,
  Radio,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import {
  IconAdjustmentsHorizontal,
  IconArrowRight,
  IconListCheck,
  IconPlus,
  IconSearch,
} from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { CurationHelp } from '../../components/Workspace/CurationHelp';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCreateLibrary, useLibraries } from '../../hooks/api/useLibraryManagement';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import classes from './Libraries.module.css';
import { TitleOverridePicker } from './libraryForm';
import {
  defaultLibraryConfiguration,
  type MaxMinimumAgeValue,
  maxMinimumAgeOptions,
} from './libraryOptions';
import { publicationStatus } from './libraryRefresh';

export function Libraries() {
  const libraries = useLibraries();
  const create = useCreateLibrary();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [opened, setOpened] = useState(false);
  const [name, setName] = useState('');
  const [ratingAge, setRatingAge] = useState<MaxMinimumAgeValue>('18');
  const [selection, setSelection] = useState<'Rules' | 'IncludeOnly'>('Rules');
  const [systems, setSystems] = useState<string[]>([]);
  const [games, setGames] = useState<string[]>([]);
  const platforms = usePlatforms();
  const filtered = (libraries.data ?? []).filter((l) =>
    l.name.toLowerCase().includes(search.toLowerCase()),
  );
  return (
    <div className={classes.page}>
      <div className={classes.hero}>
        <Group
          justify="space-between"
          align="flex-start"
          gap="xl"
        >
          <Group gap="xs">
            <Title
              order={1}
              className={classes.heading}
            >
              Libraries
            </Title>
            <CurationHelp concept="Libraries" />
          </Group>
          <Button
            {...workspaceActionProps}
            leftSection={<IconPlus size={18} />}
            onClick={() => {
              setName('');
              setRatingAge('18');
              setSelection('Rules');
              setSystems([]);
              setGames([]);
              create.reset();
              setOpened(true);
            }}
          >
            New library
          </Button>
        </Group>
      </div>
      <Group
        justify="space-between"
        mb="md"
      >
        <Text
          size="sm"
          c="dimmed"
        >
          {libraries.isSuccess
            ? `${filtered.length} ${filtered.length === 1 ? 'library' : 'libraries'}`
            : 'Libraries'}
        </Text>
        <TextInput
          aria-label="Search libraries"
          placeholder="Find a library"
          leftSection={<IconSearch size={16} />}
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          w={250}
        />
      </Group>
      {libraries.isError ? (
        <Alert
          color="red"
          title="Libraries could not be loaded"
        >
          <Button
            variant="subtle"
            onClick={() => libraries.refetch()}
          >
            Try again
          </Button>
        </Alert>
      ) : libraries.isLoading ? (
        <Stack gap="sm">
          {[
            1,
            2,
            3,
          ].map((i) => (
            <Skeleton
              key={i}
              h={88}
              radius="md"
            />
          ))}
        </Stack>
      ) : filtered.length === 0 ? (
        <div className={classes.empty}>
          <Title order={3}>{search ? 'No matching libraries' : 'No libraries yet'}</Title>
          {search && (
            <Button
              variant="subtle"
              onClick={() => setSearch('')}
            >
              Clear search
            </Button>
          )}
        </div>
      ) : (
        <ul className={classes.libraryList}>
          {filtered.map((library) => (
            <li key={library.id}>
              <Link
                to={`/libraries/${library.id}`}
                className={classes.libraryRow}
              >
                <div>
                  <Group gap="sm">
                    <Text
                      fw={650}
                      size="sm"
                    >
                      {library.name}
                    </Text>
                    {library.isDefault && (
                      <Badge
                        color="gray"
                        variant="light"
                      >
                        Default library
                      </Badge>
                    )}
                    {publicationStatus(library) && (
                      <Badge
                        color={library.configurationState !== 'Valid' ? 'red' : 'yellow'}
                        variant="light"
                      >
                        {publicationStatus(library)}
                      </Badge>
                    )}
                  </Group>
                  <Text
                    c="dimmed"
                    size="sm"
                    mt={4}
                  >
                    {library.configuration?.titleSelectionMode === 'IncludeOnly'
                      ? 'Handpicked games'
                      : 'Selected by rules'}
                  </Text>
                </div>
                <Text
                  size="sm"
                  className={classes.rowPolicy}
                >
                  {!library.configuration
                    ? 'Review rating policy'
                    : library.configuration.contentRatingPolicy?.maxMinimumAge == null
                      ? 'No rating-age ceiling'
                      : `Rating age up to ${library.configuration.contentRatingPolicy.maxMinimumAge}`}
                </Text>
                <Stack gap={2}>
                  <Text size="sm">
                    {library.publishedGameCount != null
                      ? `${library.publishedGameCount.toLocaleString()} ${library.publishedGameCount === 1 ? 'game' : 'games'} · ${library.itemCount.toLocaleString()} ${library.itemCount === 1 ? 'release' : 'releases'}`
                      : 'Not published'}
                  </Text>
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {library.collectionIds == null
                      ? 'Collections unavailable'
                      : `${library.collectionIds.length} ${library.collectionIds.length === 1 ? 'collection' : 'collections'}`}
                  </Text>
                </Stack>
                <IconArrowRight
                  size={18}
                  aria-hidden="true"
                />
              </Link>
            </li>
          ))}
        </ul>
      )}
      <Modal
        opened={opened}
        onClose={() => {
          if (!create.isPending) setOpened(false);
        }}
        title="New library"
        size="lg"
        radius="lg"
        centered
      >
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            const library = await create
              .mutateAsync({
                name: name.trim(),
                configuration: {
                  ...defaultLibraryConfiguration,
                  titleSelectionMode: selection,
                  allowedSystemKeys: selection === 'Rules' ? systems : [],
                  includeTitleIds: selection === 'IncludeOnly' ? games : [],
                  contentRatingPolicy: {
                    ...defaultLibraryConfiguration.contentRatingPolicy,
                    maxMinimumAge: ratingAge === 'none' ? null : Number(ratingAge),
                  },
                },
              })
              .catch(() => null);
            if (library) navigate(`/libraries/${library.id}?tab=games`);
          }}
        >
          <Stack>
            <TextInput
              label="Library name"
              autoFocus
              required
              maxLength={200}
              value={name}
              onChange={(e) => setName(e.currentTarget.value)}
            />
            <Radio.Group
              label="How should games be selected?"
              name="library-selection-mode"
              value={selection}
              onChange={(value) => setSelection(value === 'IncludeOnly' ? 'IncludeOnly' : 'Rules')}
            >
              <SimpleGrid
                cols={{
                  base: 1,
                  xs: 2,
                }}
                spacing="sm"
                mt="xs"
              >
                <Radio.Card
                  value="Rules"
                  className={classes.selectionCard}
                >
                  <Group
                    gap="xs"
                    wrap="nowrap"
                  >
                    <IconAdjustmentsHorizontal
                      size={18}
                      aria-hidden="true"
                    />
                    <Text
                      size="sm"
                      fw={650}
                    >
                      Select with rules
                    </Text>
                    <Radio.Indicator ml="auto" />
                  </Group>
                  <Text
                    c="dimmed"
                    size="xs"
                  >
                    Add matching games automatically.
                  </Text>
                </Radio.Card>
                <Radio.Card
                  value="IncludeOnly"
                  className={classes.selectionCard}
                >
                  <Group
                    gap="xs"
                    wrap="nowrap"
                  >
                    <IconListCheck
                      size={18}
                      aria-hidden="true"
                    />
                    <Text
                      size="sm"
                      fw={650}
                    >
                      Handpick games
                    </Text>
                    <Radio.Indicator ml="auto" />
                  </Group>
                  <Text
                    c="dimmed"
                    size="xs"
                  >
                    Include only the games you select.
                  </Text>
                </Radio.Card>
              </SimpleGrid>
            </Radio.Group>
            {selection === 'Rules' ? (
              <MultiSelect
                label="Systems"
                placeholder={systems.length ? undefined : 'All systems'}
                searchable
                clearable
                comboboxProps={{
                  withinPortal: false,
                }}
                value={systems}
                onChange={setSystems}
                data={(platforms.data ?? []).map((platform) => ({
                  value: platform.key,
                  label: platform.name,
                }))}
              />
            ) : (
              <TitleOverridePicker
                label="Find games"
                localFirst
                withinPortal={false}
                value={games}
                onChange={setGames}
              />
            )}
            <Select
              label="Maximum content-rating age"
              description="Unrated games are held for review."
              data={maxMinimumAgeOptions}
              comboboxProps={{
                withinPortal: false,
              }}
              value={ratingAge}
              allowDeselect={false}
              onChange={(value) => {
                if (value) setRatingAge(value as MaxMinimumAgeValue);
              }}
            />
            {create.isError && <Alert color="red">{create.error.message}</Alert>}
            <Group justify="flex-end">
              <Button
                variant="subtle"
                color="gray"
                disabled={create.isPending}
                onClick={() => setOpened(false)}
              >
                Cancel
              </Button>
              <Button
                type="submit"
                {...workspaceActionProps}
                disabled={!name.trim()}
                loading={create.isPending}
              >
                Create draft
              </Button>
            </Group>
          </Stack>
        </form>
      </Modal>
    </div>
  );
}
