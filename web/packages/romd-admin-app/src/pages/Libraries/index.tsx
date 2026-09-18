import {
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  Select,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { IconArrowRight, IconPlus, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { CurationHelp } from '../../components/Workspace/CurationHelp';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCreateLibrary, useLibraries } from '../../hooks/api/useLibraryManagement';
import classes from './Libraries.module.css';
import {
  defaultLibraryConfiguration,
  type MaxMinimumAgeValue,
  maxMinimumAgeOptions,
} from './libraryOptions';

export function Libraries() {
  const libraries = useLibraries();
  const create = useCreateLibrary();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [opened, setOpened] = useState(false);
  const [name, setName] = useState('');
  const [ratingAge, setRatingAge] = useState<MaxMinimumAgeValue>('18');
  const [selection, setSelection] = useState<'Rules' | 'IncludeOnly'>('Rules');
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
        <Text size="sm" c="dimmed">{libraries.isSuccess ? `${filtered.length} ${filtered.length === 1 ? 'library' : 'libraries'}` : 'Libraries'}</Text>
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
          <Title order={3}>
            {search ? 'No matching libraries' : 'No libraries yet'}
          </Title>
          {search && <Button variant="subtle" onClick={() => setSearch('')}>Clear search</Button>}
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
                    {(library.configurationState !== 'Valid' || library.needsMaterialization) && (
                      <Badge
                        color={library.configurationState !== 'Valid' ? 'red' : 'yellow'}
                        variant="light"
                      >
                        {library.configurationState !== 'Valid'
                          ? 'Needs attention'
                          : 'Updating games'}
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
                  <Text size="sm">{library.itemCount.toLocaleString()} {library.itemCount === 1 ? 'title' : 'titles'}</Text>
                  <Text size="xs" c="dimmed">{library.collectionIds == null ? 'Collections unavailable' : `${library.collectionIds.length} ${library.collectionIds.length === 1 ? 'collection' : 'collections'}`}</Text>
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
        onClose={() => setOpened(false)}
        title="New library"
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
              placeholder="e.g. Everyone or Kids"
              autoFocus
              required
              maxLength={200}
              value={name}
              onChange={(e) => setName(e.currentTarget.value)}
            />
            <Select
              label="Initial titles"
              data={[{ value: 'Rules', label: 'Owned titles matching the rating policy' }, { value: 'IncludeOnly', label: 'None - choose titles individually' }]}
              value={selection}
              allowDeselect={false}
              onChange={(value) => { if (value === 'Rules' || value === 'IncludeOnly') setSelection(value); }}
            />
            <Select
              label="Maximum content-rating age"
              description="Strictest available rating across boards, including ESRB."
              data={maxMinimumAgeOptions}
              value={ratingAge}
              allowDeselect={false}
              onChange={(value) => {
                if (value) setRatingAge(value as MaxMinimumAgeValue);
              }}
            />
            <Text
              size="xs"
              c="dimmed"
            >
              No collections attached. Unrated titles are hidden pending review.
            </Text>
            {create.isError && <Alert color="red">{create.error.message}</Alert>}
            <Button
              type="submit"
              {...workspaceActionProps}
              disabled={!name.trim()}
              loading={create.isPending}
            >
              Create library
            </Button>
          </Stack>
        </form>
      </Modal>
    </div>
  );
}
