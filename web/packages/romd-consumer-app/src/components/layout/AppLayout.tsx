import {
  ActionIcon,
  AppShell,
  Avatar,
  Box,
  Burger,
  Group,
  Menu,
  Modal,
  Stack,
  Text,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { romdLayout } from '@romd/foundation';
import romdWordmark from '@romd/foundation/brand/romd-wordmark.svg';
import romdWordmarkDark from '@romd/foundation/brand/romd-wordmark-cream.svg';
import { IconChevronDown, IconLogout, IconSearch } from '@tabler/icons-react';
import { type FormEvent, Suspense, useEffect, useState } from 'react';
import { Link, Outlet, useLocation, useNavigate } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { readReturnContext } from '../navigation/titleNavigation';
import { useReturnScrollRestoration } from '../navigation/useReturnScrollRestoration';
import { StatusState } from '../status/StatusState';

const navItems = [
  {
    label: 'Home',
    path: '/',
  },
  {
    label: 'Browse',
    path: '/library',
  },
];

function isNavActive(path: string, pathname: string, state: unknown): boolean {
  if (pathname.startsWith('/titles/'))
    return path === (readReturnContext(state).section === 'home' ? '/' : '/library');
  if (path === '/') {
    return pathname === '/' || pathname.startsWith('/collections');
  }

  if (path === '/activity') {
    return pathname.startsWith('/activity');
  }

  return pathname.startsWith('/library') || pathname.startsWith('/titles/');
}

export function AppLayout() {
  const [opened, { toggle, close }] = useDisclosure();
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  useReturnScrollRestoration();
  const [searchOpened, setSearchOpened] = useState(false);
  const [search, setSearch] = useState('');

  // Keep the header field in step with the library page's ?q= param so it
  // never shows a stale query after navigation.
  useEffect(() => {
    setSearch(new URLSearchParams(location.search).get('q') ?? '');
  }, [
    location.search,
  ]);

  const handleLogout = () => {
    logout();
    navigate('/login', {
      replace: true,
    });
  };

  const handleSearchSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const query = search.trim();
    navigate(query ? `/library?q=${encodeURIComponent(query)}` : '/library');
    close();
    setSearchOpened(false);
  };

  return (
    <AppShell
      className="romd-consumer-shell"
      header={{
        height: romdLayout.headerHeight,
      }}
      navbar={{
        width: 220,
        breakpoint: 'sm',
        collapsed: {
          desktop: true,
          mobile: !opened,
        },
      }}
      padding={0}
      styles={{
        root: {
          background: 'transparent',
        },
        header: {
          background: 'color-mix(in srgb, var(--romd-bg) 72%, transparent)',
          borderColor: 'var(--romd-border-subtle)',
          backdropFilter: 'blur(18px) saturate(1.3)',
        },
        navbar: {
          background: 'color-mix(in srgb, var(--romd-bg) 97%, transparent)',
          borderColor: 'var(--romd-border-subtle)',
        },
      }}
    >
      <AppShell.Header>
        <Box
          h="100%"
          px={romdLayout.headerPadding}
          style={{
            display: 'grid',
            gridTemplateColumns: 'auto minmax(0, 1fr) auto',
            alignItems: 'center',
            gap: romdLayout.headerControlGap,
          }}
        >
          {/* Left — brand + primary nav */}
          <Group
            gap="lg"
            wrap="nowrap"
            style={{
              justifySelf: 'start',
              minWidth: 0,
            }}
          >
            <Group
              gap={romdLayout.headerControlGap}
              wrap="nowrap"
            >
              <Burger
                aria-label={opened ? 'Close navigation' : 'Open navigation'}
                opened={opened}
                onClick={toggle}
                hiddenFrom="sm"
                size="sm"
                w={romdLayout.headerControlSize}
                h={romdLayout.headerControlSize}
              />

              <Box
                component={Link}
                to="/"
                className="romd-brand"
                aria-label="ROMD home"
                style={{
                  textDecoration: 'none',
                  color: 'inherit',
                }}
              >
                <span
                  className="romd-brand-icon"
                  aria-hidden="true"
                />
                <Box
                  component="span"
                  className="romd-brand-wordmark"
                  visibleFrom="xs"
                  aria-hidden="true"
                >
                  <img
                    className="romd-brand-wordmark-light"
                    src={romdWordmark}
                    alt=""
                  />
                  <img
                    className="romd-brand-wordmark-dark"
                    src={romdWordmarkDark}
                    alt=""
                  />
                </Box>
              </Box>
            </Group>

            <Group
              gap={4}
              wrap="nowrap"
              visibleFrom="sm"
            >
              {navItems.map((item) => (
                <Text
                  key={item.path}
                  className="romd-primary-nav"
                  aria-current={
                    isNavActive(item.path, location.pathname, location.state) ? 'page' : undefined
                  }
                  component={Link}
                  to={item.path}
                  fw={600}
                  fz="sm"
                  px="sm"
                  py={6}
                  c={
                    isNavActive(item.path, location.pathname, location.state)
                      ? 'var(--romd-text-strong)'
                      : 'dimmed'
                  }
                  style={{
                    borderRadius: 'var(--mantine-radius-sm)',
                    textDecoration: 'none',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {item.label}
                </Text>
              ))}
            </Group>
          </Group>

          <Box />

          {/* Right — user */}
          <Group
            gap={romdLayout.headerControlGap}
            wrap="nowrap"
            style={{
              justifySelf: 'end',
            }}
          >
            {location.pathname !== '/library' && (
              <ActionIcon
                variant="subtle"
                color="gray"
                size={romdLayout.headerControlSize}
                aria-label="Search games"
                onClick={() => setSearchOpened(true)}
              >
                <IconSearch size={20} />
              </ActionIcon>
            )}
            <Menu
              position="bottom-end"
              width={240}
              withArrow
            >
              <Menu.Target>
                <Group
                  component="button"
                  aria-label="Account"
                  gap={romdLayout.headerControlGap}
                  h={romdLayout.headerControlSize}
                  p={0}
                  wrap="nowrap"
                  style={{
                    border: 0,
                    background: 'transparent',
                    color: 'inherit',
                    cursor: 'pointer',
                  }}
                >
                  <Avatar
                    size={32}
                    radius="xl"
                    color="mint"
                  >
                    {user?.email.at(0)?.toUpperCase() ?? 'U'}
                  </Avatar>
                  <IconChevronDown
                    size={16}
                    color="var(--mantine-color-dark-2)"
                    aria-hidden
                  />
                </Group>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item
                  component={Link}
                  to="/connections"
                >
                  Connections
                </Menu.Item>
                <Menu.Item
                  component={Link}
                  to="/activity"
                >
                  Play history
                </Menu.Item>
                <Menu.Label>{user?.email ?? 'Local user'}</Menu.Label>
                <Menu.Item
                  leftSection={<IconLogout size={16} />}
                  onClick={handleLogout}
                >
                  Sign out
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Box>
      </AppShell.Header>

      <Modal
        opened={searchOpened}
        onClose={() => setSearchOpened(false)}
        title="Search your games"
        centered
      >
        <Box
          component="form"
          onSubmit={handleSearchSubmit}
          w="100%"
          maw={480}
          style={{
            justifySelf: 'center',
          }}
        >
          <Group
            gap="sm"
            px="sm"
            h={40}
            wrap="nowrap"
            className="romd-header-search"
            style={{
              border: '1px solid var(--romd-border)',
              borderRadius: 'var(--mantine-radius-md)',
              background: 'var(--romd-panel)',
            }}
          >
            <IconSearch
              size={18}
              color="var(--mantine-color-dark-2)"
              aria-hidden
            />
            <Box
              component="input"
              data-autofocus
              value={search}
              onChange={(event) => setSearch(event.currentTarget.value)}
              placeholder="Search games"
              aria-label="Search"
              style={{
                flex: 1,
                minWidth: 0,
                color: 'var(--mantine-color-dark-0)',
                background: 'transparent',
                border: 0,
                outline: 0,
                font: 'inherit',
              }}
            />
          </Group>
        </Box>
        <Text
          size="xs"
          c="dimmed"
          mt="sm"
        >
          Press Enter to search your library.
        </Text>
      </Modal>

      <AppShell.Navbar p="md">
        <Stack gap="xs">
          {navItems.map((item) => (
            <Text
              key={item.path}
              component={Link}
              to={item.path}
              aria-current={
                isNavActive(item.path, location.pathname, location.state) ? 'page' : undefined
              }
              fw={600}
              p="sm"
              c={isNavActive(item.path, location.pathname, location.state) ? 'mint.4' : undefined}
              style={{
                borderRadius: 'var(--mantine-radius-sm)',
                textDecoration: 'none',
              }}
              onClick={close}
            >
              {item.label}
            </Text>
          ))}
        </Stack>
      </AppShell.Navbar>

      <AppShell.Main>
        <Box
          px={{
            base: 'md',
            sm: 'xl',
          }}
          py="xl"
          maw={
            location.pathname === '/' || /^\/titles\/[^/]+$/.test(location.pathname)
              ? 'none'
              : 'var(--romd-content-max)'
          }
          mx="auto"
        >
          <Suspense
            fallback={
              <StatusState
                kind="loading"
                label="Loading page"
              />
            }
          >
            <Outlet />
          </Suspense>
        </Box>
      </AppShell.Main>
    </AppShell>
  );
}
