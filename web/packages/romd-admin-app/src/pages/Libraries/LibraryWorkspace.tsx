import {
  Alert,
  Avatar,
  Badge,
  Button,
  Group,
  Menu,
  Modal,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Tabs,
  Text,
  Title,
} from '@mantine/core';
import {
  IconArrowLeft,
  IconArrowRight,
  IconDots,
  IconEye,
  IconRefresh,
  IconSettings,
  IconTrash,
  IconUsers,
} from '@tabler/icons-react';
import { useCallback, useState } from 'react';
import {
  Link,
  useBeforeUnload,
  useBlocker,
  useNavigate,
  useParams,
  useSearchParams,
} from 'react-router';
import { CurationHelp } from '../../components/Workspace/CurationHelp';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useLibraryAttachments } from '../../hooks/api/useLibraryExperience';
import {
  useDeleteLibrary,
  useForceMaterializeLibrary,
  useLibrary,
} from '../../hooks/api/useLibraryManagement';
import { useAssignUserLibrary, useUsers } from '../../hooks/api/useUsers';
import classes from './Libraries.module.css';
import { LibraryCollections } from './LibraryCollections';
import { LibraryGames } from './LibraryGames';
import { LibraryPreview } from './LibraryPreview';
import { LibraryRules } from './LibraryRules';

const tabs = [
  'overview',
  'games',
  'collections',
  'access',
  'preview',
] as const;

function Audience({ libraryId }: { libraryId: string }) {
  const users = useUsers();
  const assign = useAssignUserLibrary();
  const [selected, setSelected] = useState<string | null>(null);
  const members = (users.data ?? []).filter((u) => u.libraryId === libraryId);
  return (
    <Stack
      className={classes.panel}
      gap="md"
    >
      <Group>
        <IconUsers size={20} />
        <Title order={3}>People in this audience</Title>
      </Group>
      <Text
        size="sm"
        c="dimmed"
      >
        Each person is assigned one library, wherever they play.
      </Text>
      {users.isError ? (
        <Alert color="red">Could not load audience members.</Alert>
      ) : users.isPending ? (
        <Skeleton h={60} />
      ) : members.length === 0 ? (
        <Text
          c="dimmed"
          size="sm"
        >
          No one is assigned yet.
        </Text>
      ) : (
        members.map((user) => (
          <Group
            key={user.id}
            justify="space-between"
          >
            <Group>
              <Avatar
                color="teal"
                radius="xl"
              >
                {user.email.slice(0, 1).toUpperCase()}
              </Avatar>
              <Text size="sm">{user.email}</Text>
            </Group>
            <Button
              variant="subtle"
              size="xs"
              color="gray"
              loading={assign.isPending}
              onClick={() =>
                assign.mutate({
                  userId: user.id,
                  libraryId: null,
                })
              }
            >
              Unassign
            </Button>
          </Group>
        ))
      )}
      <Group align="end">
        <Select
          flex={1}
          searchable
          label="Assign a person"
          placeholder="Choose a person"
          data={(users.data ?? [])
            .filter((u) => u.libraryId !== libraryId)
            .map((u) => ({
              value: u.id,
              label: `${u.email}${u.libraryId ? ' · Move from another library' : ''}`,
            }))}
          value={selected}
          onChange={setSelected}
        />
        <Button
          {...workspaceActionProps}
          disabled={!selected}
          loading={assign.isPending}
          onClick={async () => {
            if (selected) {
              try {
                await assign.mutateAsync({
                  userId: selected,
                  libraryId,
                });
                setSelected(null);
              } catch {
                /* Visible error. */
              }
            }
          }}
        >
          Assign
        </Button>
      </Group>
      {assign.isError && <Alert color="red">{assign.error.message}</Alert>}
      <Button
        component={Link}
        to="/users"
        variant="subtle"
        color="gray"
        size="xs"
        w="fit-content"
      >
        Manage people and roles
      </Button>
    </Stack>
  );
}

export function LibraryWorkspace() {
  const { libraryId = '' } = useParams();
  const query = useLibrary(libraryId);
  const attachments = useLibraryAttachments(libraryId);
  const rebuild = useForceMaterializeLibrary();
  const remove = useDeleteLibrary();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const active = tabs.find((t) => t === params.get('tab')) ?? 'overview';
  const [dirty, setDirty] = useState(false);
  const blocker = useBlocker(dirty);
  const [discardSettings, setDiscardSettings] = useState(false);
  useBeforeUnload(
    useCallback(
      (event: BeforeUnloadEvent) => {
        if (dirty) {
          event.preventDefault();
          event.returnValue = '';
        }
      },
      [
        dirty,
      ],
    ),
  );
  const [settings, setSettings] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const go = (tab: string) => {
    const next = new URLSearchParams(params);
    next.set('tab', tab);
    setParams(next);
  };
  if (query.isPending)
    return (
      <Skeleton
        h={360}
        radius="lg"
      />
    );
  if (query.isError || !query.data)
    return (
      <Alert
        color="red"
        title="Library unavailable"
      >
        <Text>This library could not be loaded.</Text>
        <Button
          component={Link}
          to="/libraries"
          variant="subtle"
        >
          Back to libraries
        </Button>
      </Alert>
    );
  const library = query.data;
  const rows = attachments.data ?? [];
  return (
    <div className={classes.page}>
      <Button
        component={Link}
        to="/libraries"
        variant="subtle"
        color="gray"
        leftSection={<IconArrowLeft size={15} />}
        px={0}
      >
        All libraries
      </Button>
      <div className={classes.hero}>
        <Group
          justify="space-between"
          align="flex-start"
        >
          <Stack gap="sm">
            <Group>
              {library.isDefault && (
                <Badge
                  color="gray"
                  size="sm"
                >
                  Default
                </Badge>
              )}
            </Group>
            <Group gap="xs"><Title
              order={1}
              className={classes.heading}
            >
              {library.name}
            </Title>
            <CurationHelp concept="Libraries" /></Group>
          </Stack>
          <Group>
            <Button
              variant="light"
              {...workspaceActionProps}
              leftSection={<IconEye size={17} />}
              onClick={() => go('preview')}
            >
              Preview audience
            </Button>
            <Menu position="bottom-end">
              <Menu.Target>
                <Button
                  variant="subtle"
                  color="gray"
                  aria-label="Library actions"
                  px="xs"
                >
                  <IconDots size={20} />
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item
                  disabled={dirty}
                  leftSection={<IconSettings size={16} />}
                  onClick={() => setSettings(true)}
                >
                  Library details
                </Menu.Item>
                <Menu.Item
                  leftSection={<IconRefresh size={16} />}
                  disabled={rebuild.isPending}
                  onClick={() => rebuild.mutate(libraryId)}
                >
                  Refresh library results
                </Menu.Item>
                <Menu.Divider />
                <Menu.Item
                  color="red"
                  disabled={library.isDefault}
                  leftSection={<IconTrash size={16} />}
                  onClick={() => setDeleting(true)}
                >
                  Delete library
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Group>
      </div>
      {library.configurationState !== 'Valid' && (
        <Alert
          color="red"
          title="This audience needs attention"
          mb="lg"
        >
          {library.configurationError ?? 'Review and save the library rules to restore access.'}
        </Alert>
      )}
      {library.needsMaterialization && (
        <Alert
          color="teal"
          mb="lg"
          title="Updating this audience"
        >
          Your saved rules are being applied. Consumer access resumes when this update completes.
        </Alert>
      )}
      {rebuild.isError && (
        <Alert
          color="red"
          mb="lg"
        >
          {rebuild.error.message}
        </Alert>
      )}
      <Tabs
        value={active}
        onChange={(v) => {
          if (v) go(v);
        }}
        color="teal"
        className={classes.tabs}
        classNames={{
          list: classes.tabsList,
          tab: classes.tab,
        }}
      >
        <Tabs.List>
          {tabs.map((tab) => (
            <Tabs.Tab
              key={tab}
              value={tab}
            >
              {tab === 'access' ? 'People' : tab.charAt(0).toUpperCase() + tab.slice(1)}
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>
      {active === 'overview' && (
        <Stack gap="xl">
          <SimpleGrid
            cols={{
              base: 1,
              sm: 3,
            }}
            spacing="lg"
          >
            <div className={classes.overviewStat}>
              <Text size="sm" c="dimmed">Titles in library</Text>
              <Text
                className={classes.stat}
                mt="sm"
              >
                {library.itemCount.toLocaleString()}
              </Text>
              <Text
                c="dimmed"
                size="sm"
                mt="sm"
              >
                {library.configuration?.titleSelectionMode === 'IncludeOnly'
                  ? 'Handpicked for this audience'
                  : 'Selected by your library rules'}
              </Text>
            </div>
            <div className={classes.overviewStat}>
              <Text size="sm" c="dimmed">Attached collections</Text>
              <Text
                className={classes.stat}
                mt="sm"
              >
                {attachments.isSuccess ? rows.length : '—'}
              </Text>
              <Text
                c="dimmed"
                size="sm"
                mt="sm"
              >
                {attachments.isError
                  ? 'Could not load collections'
                  : `${rows.filter((r) => r.isFeatured).length} featured in discovery`}
              </Text>
            </div>
            <div className={classes.overviewStat}>
              <Text size="sm" c="dimmed">Content ratings</Text>
              <Text
                className={classes.stat}
                mt="sm"
              >
                {!library.configuration
                  ? 'Review rules'
                  : library.configuration.contentRatingPolicy?.maxMinimumAge == null
                    ? 'No ceiling'
                    : `Up to ${library.configuration.contentRatingPolicy.maxMinimumAge}`}
              </Text>
              <Text
                c="dimmed"
                size="sm"
                mt="sm"
              >
                {library.configuration?.contentRatingPolicy?.maxMinimumAge == null
                  ? 'No rating-age ceiling; other rules still apply'
                  : 'Maximum content-rating age'}
              </Text>
              <Button
                variant="subtle"
                {...workspaceActionProps}
                size="compact-sm"
                mt="md"
                onClick={() => go('games')}
              >
                Edit content ratings
              </Button>
            </div>
          </SimpleGrid>
          <div className={classes.overviewSection}>
            <Group
              justify="space-between"
              mb="xl"
            >
              <div>
                <Title
                  order={2}
                  className={classes.sectionHeading}
                >
                  Collections
                </Title>
              </div>
              <Button
                variant="subtle"
                {...workspaceActionProps}
                rightSection={<IconArrowRight size={17} />}
                onClick={() => go('collections')}
              >
                Arrange collections
              </Button>
            </Group>
            {rows.length > 0 ? (
              <Stack>
                {rows.slice(0, 4).map((row) => (
                  <Group
                    key={row.collectionId}
                    justify="space-between"
                  >
                    <Text component={Link} to={`/collections/${row.collectionId}`} fw={550}>{row.name}</Text>
                    <Text
                      size="sm"
                      c="dimmed"
                    >
                      {row.visibleCount} {row.visibleCount === 1 ? 'title' : 'titles'} visible{row.isFeatured ? ' · Featured' : ''}
                    </Text>
                  </Group>
                ))}
              </Stack>
            ) : (
              <Text c="dimmed">No collections attached</Text>
            )}
          </div>
          <Group justify="space-between">
            <Text
              size="sm"
              c="dimmed"
            >
              {library.lastMaterializedAt
                ? `Library results updated ${new Date(library.lastMaterializedAt).toLocaleString()}`
                : 'Waiting for the first library update.'}
            </Text>
            {(library.configurationState !== 'Valid' || library.needsMaterialization) && (
              <Badge color={library.configurationState !== 'Valid' ? 'red' : 'yellow'}>
                {library.configurationState !== 'Valid' ? 'Needs attention' : 'Updating games'}
              </Badge>
            )}
          </Group>
        </Stack>
      )}
      {active === 'games' && (
        <LibraryGames
          key={libraryId}
          library={library}
          onDirtyChange={setDirty}
        />
      )}
      {active === 'collections' && <LibraryCollections libraryId={libraryId} />}
      {active === 'access' && <Audience libraryId={libraryId} />}
      {active === 'preview' && !library.needsMaterialization && (
        <LibraryPreview
          libraryId={libraryId}
          name={library.name}
        />
      )}
      <Modal
        opened={settings}
        onClose={() => {
          if (dirty) setDiscardSettings(true);
          else setSettings(false);
        }}
        title="Library details"
        radius="lg"
        size="lg"
      >
        <LibraryRules
          key={`${libraryId}-${settings}`}
          library={library}
          onDirtyChange={setDirty}
          section="settings"
        />
      </Modal>
      <Modal
        opened={blocker.state === 'blocked' || discardSettings}
        onClose={() => {
          if (blocker.state === 'blocked') blocker.reset();
          setDiscardSettings(false);
        }}
        title="Leave unsaved changes?"
        radius="lg"
        centered
      >
        <Stack>
          <Text>Your edits have not been saved. Stay here to finish, or discard them.</Text>
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => {
                if (blocker.state === 'blocked') blocker.reset();
                setDiscardSettings(false);
              }}
            >
              Keep editing
            </Button>
            <Button
              color="red"
              onClick={() => {
                setDirty(false);
                if (blocker.state === 'blocked') blocker.proceed();
                if (discardSettings) setSettings(false);
                setDiscardSettings(false);
              }}
            >
              Discard changes
            </Button>
          </Group>
        </Stack>
      </Modal>
      <Modal
        opened={deleting}
        onClose={() => setDeleting(false)}
        title={`Delete ${library.name}?`}
        radius="lg"
      >
        <Stack>
          <Text>
            People assigned to this library will lose access. Shared collections and stored games
            are retained.
          </Text>
          {remove.isError && <Alert color="red">{remove.error.message}</Alert>}
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => setDeleting(false)}
            >
              Cancel
            </Button>
            <Button
              color="red"
              loading={remove.isPending}
              onClick={async () => {
                try {
                  await remove.mutateAsync(libraryId);
                  navigate('/libraries');
                } catch {
                  /* Visible error. */
                }
              }}
            >
              Delete library
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
