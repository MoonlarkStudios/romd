import { ActionIcon, AppShell, Avatar, Box, Burger, Group, Menu, Modal, Stack, Text, Title } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconChevronDown, IconDeviceGamepad2, IconLogout, IconSearch } from '@tabler/icons-react';
import { type FormEvent, useEffect, useState } from 'react';
import { Link, Outlet, useLocation, useNavigate } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { readReturnContext } from '../navigation/titleNavigation';
import { useReturnScrollRestoration } from '../navigation/useReturnScrollRestoration';

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
  if (pathname.startsWith('/titles/')) return path === (readReturnContext(state).section === 'home' ? '/' : '/library');
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
  }, [location.search]);

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
      header={{ height: 64 }}
      navbar={{
        width: 220,
        breakpoint: 'sm',
        collapsed: { desktop: true, mobile: !opened },
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
          px={{ base: 'md', sm: 'xl' }}
          style={{
            display: 'grid',
            gridTemplateColumns: 'auto minmax(0, 1fr) auto',
            alignItems: 'center',
            gap: 'var(--mantine-spacing-md)',
          }}
        >
          {/* Left — brand + primary nav */}
          <Group
            gap="lg"
            wrap="nowrap"
            style={{ justifySelf: 'start', minWidth: 0 }}
          >
            <Burger
              aria-label={opened ? "Close navigation" : "Open navigation"}
              opened={opened}
              onClick={toggle}
              hiddenFrom="sm"
              size="sm"
            />

            <Box
              component={Link}
              to="/"
              style={{ textDecoration: 'none', color: 'inherit' }}
            >
              <Group
                gap="sm"
                wrap="nowrap"
              >
                <Box
                  style={{
                    width: 34,
                    height: 34,
                    borderRadius: 'var(--mantine-radius-sm)',
                    display: 'grid',
                    placeItems: 'center',
                    background: 'linear-gradient(150deg, var(--romd-accent), var(--romd-sea))',
                    color: 'var(--romd-on-accent)',
                  }}
                >
                  <IconDeviceGamepad2
                    size={20}
                    stroke={2.2}
                  />
                </Box>
                <Title
                  order={3}
                  className="romd-wordmark"
                  visibleFrom="xs"
                >
                  ROMD
                </Title>
              </Group>
            </Box>

            <Group
              gap={4}
              wrap="nowrap"
              visibleFrom="sm"
            >
              {navItems.map((item) => (
                <Text
                  key={item.path}
                  className="romd-primary-nav"
                  aria-current={isNavActive(item.path, location.pathname, location.state) ? "page" : undefined}
                  component={Link}
                  to={item.path}
                  fw={600}
                  fz="sm"
                  px="sm"
                  py={6}
                  c={isNavActive(item.path, location.pathname, location.state) ? 'var(--romd-text-strong)' : 'dimmed'}
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
            gap="xs"
            wrap="nowrap"
            style={{ justifySelf: 'end' }}
          >
            {location.pathname !== '/library' && <ActionIcon variant="subtle" color="gray" size="lg" aria-label="Search games" onClick={() => setSearchOpened(true)}><IconSearch size={21} /></ActionIcon>}
            <Menu
              position="bottom-end"
              width={240}
              withArrow
            >
              <Menu.Target>
                <Group
                  component="button"
                  aria-label="Account"
                  gap="xs"
                  wrap="nowrap"
                  style={{ border: 0, background: "transparent", color: "inherit", cursor: 'pointer' }}
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
                <Menu.Item component={Link} to="/activity">Play history</Menu.Item>
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

<Modal opened={searchOpened} onClose={() => setSearchOpened(false)} title="Search your games" centered>
<Box
            component="form"
            onSubmit={handleSearchSubmit}
            w="100%"
            maw={480}
            style={{ justifySelf: 'center' }}
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
<Text size="xs" c="dimmed" mt="sm">Press Enter to search your library.</Text>
</Modal>

      <AppShell.Navbar p="md">
        <Stack gap="xs">
          {navItems.map((item) => (
            <Text
              key={item.path}
              component={Link}
              to={item.path}
              aria-current={isNavActive(item.path, location.pathname, location.state) ? "page" : undefined}
              fw={600}
              p="sm"
              c={isNavActive(item.path, location.pathname, location.state) ? 'mint.4' : undefined}
              style={{ borderRadius: 'var(--mantine-radius-sm)', textDecoration: 'none' }}
              onClick={close}
            >
              {item.label}
            </Text>
          ))}
        </Stack>
      </AppShell.Navbar>

      <AppShell.Main>
        <Box
          px={{ base: 'md', sm: 'xl' }}
          py="xl"
          maw={(location.pathname === '/' || /^\/titles\/[^/]+$/.test(location.pathname)) ? 'none' : 'var(--romd-content-max)'}
          mx="auto"
        >
          <Outlet />
        </Box>
      </AppShell.Main>
    </AppShell>
  );
}
