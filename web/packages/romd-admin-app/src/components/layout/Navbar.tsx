import { Badge, Divider, NavLink, ScrollArea, Stack, Text } from '@mantine/core';
import { IconBook, IconDatabase, IconDeviceGamepad2, IconFile, IconHeartbeat, IconHistory, IconHome, IconLibrary, IconListCheck, IconPlugConnected, IconSettings, IconStackBack, IconUsers, IconWorld } from '@tabler/icons-react';
import { Link, useLocation } from 'react-router';
import { useUnroutedDats } from '../../hooks/api/useDats';
import { useLibraryStats } from '../../hooks/api/useRoms';
import { usePermissions } from '../../hooks/usePermissions';

type NavItem = { label: string; icon: typeof IconHome; path: string; badge?: number };
type NavSection = { title?: string; items: NavItem[] };

export function Navbar({ onNavigate }: { onNavigate?: () => void }) {
  const { pathname } = useLocation();
  const { canUploadRoms, canManageTitles, canManageUsers } = usePermissions();
  const unrouted = useUnroutedDats();
  const stats = useLibraryStats();
  const attention = unrouted.isSuccess && stats.isSuccess ? (unrouted.data?.length ?? 0) + Number(stats.data?.unidentifiedCount ?? 0) : undefined;
  const sections: NavSection[] = [
    { items: [{ label: 'Dashboard', icon: IconHome, path: '/' }] },
    { title: 'Sources & Files', items: [{ label: 'Systems', icon: IconDeviceGamepad2, path: '/systems' }, { label: 'ROMs', icon: IconFile, path: '/roms', badge: attention }] },
    { title: 'Curation', items: [{ label: 'Catalog', icon: IconBook, path: '/catalog' }, ...(canUploadRoms ? [{ label: 'Collections', icon: IconStackBack, path: '/collections' }] : []), ...(canManageUsers ? [{ label: 'Libraries', icon: IconLibrary, path: '/libraries' }] : [])] },
    { title: 'Operations', items: [{ label: 'Jobs', icon: IconListCheck, path: '/jobs' }, { label: 'Storage', icon: IconDatabase, path: '/storage' }, ...(canManageUsers ? [{ label: 'Diagnostics', icon: IconHeartbeat, path: '/diagnostics' }] : [])] },
    { title: 'Administration', items: [...(canManageUsers ? [{ label: 'Users', icon: IconUsers, path: '/users' }, { label: 'Integrations', icon: IconPlugConnected, path: '/integrations' }] : []), ...(canManageTitles ? [{ label: 'Reference data', icon: IconWorld, path: '/reference-data' }] : []), ...(canManageUsers ? [{ label: 'Settings', icon: IconSettings, path: '/settings' }, { label: 'Audit log', icon: IconHistory, path: '/audit' }] : [])] },
  ];
  return <ScrollArea h="100%" type="auto" offsetScrollbars><Stack gap={0} p="md">
    {sections.filter(section => section.items.length > 0).map((section, index) => <div key={section.title ?? 'dashboard'}>
      {section.title && <>{index > 0 && <Divider my="xs" />}<Text size="xs" c="dimmed" fw={500} tt="uppercase" pl="sm" pb={5}>{section.title}</Text></>}
      {section.items.map((item) => {
        const active = pathname === item.path || (item.path !== '/' && pathname.startsWith(`${item.path}/`)) || (item.path === '/catalog' && pathname.startsWith('/titles/'));
        return <NavLink component={Link} to={item.path} key={item.path} label={item.label} leftSection={<item.icon size={20} />} rightSection={item.badge !== undefined && item.badge > 0 ? <Badge size="xs" color="orange" title={`${item.badge.toLocaleString()} items need attention`}>{item.badge.toLocaleString()}</Badge> : undefined} active={active} aria-current={active ? 'page' : undefined} onClick={onNavigate} style={{ borderRadius: 'var(--mantine-radius-sm)', marginBottom: 2 }} />;
      })}
    </div>)}
  </Stack></ScrollArea>;
}
