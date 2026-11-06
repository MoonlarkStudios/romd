import {
  ActionIcon,
  Badge,
  Burger,
  Group,
  Menu,
  Title,
  Tooltip,
  useMantineColorScheme,
} from '@mantine/core';
import { romdLayout } from '@romd/foundation';
import romdWordmark from '@romd/foundation/brand/romd-wordmark.svg';
import romdWordmarkDark from '@romd/foundation/brand/romd-wordmark-cream.svg';
import {
  IconCircleFilled,
  IconKey,
  IconLogout,
  IconMoon,
  IconSun,
  IconUser,
} from '@tabler/icons-react';
import { useNavigate } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { useHealthCheck } from '../../hooks/useHealthCheck';
import { ActivityCenter } from '../Activity/ActivityCenter';

interface HeaderProps {
  opened: boolean;
  toggle: () => void;
}

export function Header({ opened, toggle }: HeaderProps) {
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const { user, logout } = useAuth();
  const { status, data } = useHealthCheck();
  const navigate = useNavigate();

  const getStatusColor = () => {
    switch (status) {
      case 'healthy':
        return 'green';
      case 'error':
        return 'red';
      default:
        return 'gray';
    }
  };

  return (
    <Group
      h="100%"
      px={romdLayout.headerPadding}
      justify="space-between"
      wrap="nowrap"
      gap={romdLayout.headerControlGap}
    >
      <Group
        wrap="nowrap"
        gap={romdLayout.headerControlGap}
      >
        <Burger
          opened={opened}
          onClick={toggle}
          hiddenFrom="sm"
          size="sm"
          w={romdLayout.headerControlSize}
          h={romdLayout.headerControlSize}
          aria-label="Toggle navigation"
        />
        <Title
          order={3}
          className="romd-brand"
          aria-label="ROMD"
        >
          <span
            className="romd-brand-icon"
            aria-hidden="true"
          />
          <span
            className="romd-brand-wordmark"
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
          </span>
        </Title>
      </Group>
      <Group
        gap={romdLayout.headerControlGap}
        wrap="nowrap"
      >
        <Tooltip label={`API: ${data?.status ?? status}`}>
          <Badge
            visibleFrom="sm"
            size="sm"
            variant="light"
            color={getStatusColor()}
            leftSection={<IconCircleFilled size={8} />}
          >
            {status === 'healthy' ? 'Healthy' : status === 'error' ? 'Error' : 'Checking'}
          </Badge>
        </Tooltip>
        <ActivityCenter />
        <ActionIcon
          variant="subtle"
          onClick={toggleColorScheme}
          size={romdLayout.headerControlSize}
          aria-label="Toggle color scheme"
        >
          {colorScheme === 'dark' ? <IconSun size={20} /> : <IconMoon size={20} />}
        </ActionIcon>

        <Menu
          shadow="md"
          width={200}
          position="bottom-end"
        >
          <Menu.Target>
            <ActionIcon
              variant="subtle"
              size={romdLayout.headerControlSize}
              aria-label="User menu"
            >
              <IconUser size={20} />
            </ActionIcon>
          </Menu.Target>

          <Menu.Dropdown>
            {user && (
              <>
                <Menu.Label>{user.email}</Menu.Label>
                <Menu.Divider />
              </>
            )}
            <Menu.Item
              leftSection={<IconKey size={16} />}
              onClick={() => navigate('/account')}
            >
              My account
            </Menu.Item>
            <Menu.Item
              leftSection={<IconLogout size={16} />}
              onClick={logout}
              color="red"
            >
              Logout
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>
    </Group>
  );
}
