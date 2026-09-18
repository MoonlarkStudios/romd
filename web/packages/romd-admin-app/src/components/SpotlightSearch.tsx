import { Spotlight, spotlight } from '@mantine/spotlight';
import {
  IconBook,
  IconDashboard,
  IconDatabase,
  IconDeviceGamepad2,
  IconInbox,
  IconLibrary,
  IconSearch,
  IconSettings,
  IconStackBack,
  IconUpload,
  IconUsers,
  IconWorld,
} from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { usePlatforms } from '../hooks/api/usePlatforms';
import { usePermissions } from '../hooks/usePermissions';

interface SpotlightAction {
  id: string;
  label: string;
  description: string;
  leftSection: React.ReactNode;
  onClick: () => void;
}

export function SpotlightSearch() {
  const navigate = useNavigate();
  const { canUploadRoms, canManageTitles, canManageUsers } = usePermissions();
  const { data: platforms } = usePlatforms();
  const [query, setQuery] = useState('');

  const navigationActions: SpotlightAction[] = useMemo(() => {
    const all: (SpotlightAction & { visible: boolean })[] = [
      {
        id: 'dashboard',
        label: 'Dashboard',
        description: 'View system overview and stats',
        leftSection: <IconDashboard size={18} />,
        onClick: () => navigate('/'),
        visible: true,
      },
      {
        id: 'catalog',
        label: 'Catalog',
        description: 'View and manage game titles',
        leftSection: <IconBook size={18} />,
        onClick: () => navigate('/catalog'),
        visible: true,
      },
      {
        id: 'inbox',
        label: 'ROMs needing attention',
        description: 'Route unrouted DATs and review unidentified files',
        leftSection: <IconInbox size={18} />,
        onClick: () => navigate('/roms?tab=attention'),
        visible: canUploadRoms,
      },
      {
        id: 'rom-files',
        label: 'ROMs',
        description: 'Search imported ROM filenames and inspect files',
        leftSection: <IconDatabase size={18} />,
        onClick: () => navigate('/roms?tab=all'),
        visible: true,
      },
      {
        id: 'systems',
        label: 'Systems',
        description: 'Browse systems, sources, and scoped titles',
        leftSection: <IconDeviceGamepad2 size={18} />,
        onClick: () => navigate('/systems'),
        visible: true,
      },
      {
        id: 'collections',
        label: 'Collections',
        description: 'Curate cross-system collections',
        leftSection: <IconStackBack size={18} />,
        onClick: () => navigate('/collections'),
        visible: canUploadRoms,
      },
      {
        id: 'libraries',
        label: 'Libraries',
        description: 'Manage library access and materialization',
        leftSection: <IconLibrary size={18} />,
        onClick: () => navigate('/libraries'),
        visible: canManageTitles,
      },
      {
        id: 'upload',
        label: 'Import ROMs',
        description: 'Upload ROMs, archives, and DAT catalogs',
        leftSection: <IconUpload size={18} />,
        onClick: () => navigate('/roms/import'),
        visible: canUploadRoms,
      },
      {
        id: 'storage',
        label: 'Storage',
        description: 'View CAS size and compression metrics',
        leftSection: <IconDatabase size={18} />,
        onClick: () => navigate('/storage'),
        visible: true,
      },
      {
        id: 'taxonomy',
        label: 'Reference data',
        description: 'Manage regions and languages',
        leftSection: <IconWorld size={18} />,
        onClick: () => navigate('/reference-data'),
        visible: canManageTitles,
      },
      {
        id: 'users',
        label: 'Users',
        description: 'Manage user access',
        leftSection: <IconUsers size={18} />,
        onClick: () => navigate('/users'),
        visible: canManageUsers,
      },
      { id: 'account', label: 'My account', description: 'Password and active sessions', leftSection: <IconUsers size={18} />, onClick: () => navigate('/account'), visible: true },
      { id: 'integrations', label: 'Integrations', description: 'Configure metadata and artwork providers', leftSection: <IconSettings size={18} />, onClick: () => navigate('/integrations'), visible: canManageUsers },
      { id: 'audit', label: 'Audit log', description: 'Review administrative changes', leftSection: <IconSettings size={18} />, onClick: () => navigate('/audit'), visible: canManageUsers },
      {
        id: 'settings',
        label: 'Settings',
        description: 'Configure installation automation',
        leftSection: <IconSettings size={18} />,
        onClick: () => navigate('/settings'),
        visible: canManageUsers,
      },
    ];

    return all
      .filter((action) => action.visible)
      .map(({ visible: _visible, ...action }) => action);
  }, [navigate, canUploadRoms, canManageTitles, canManageUsers]);

  const actions = useMemo(() => {
    const items: SpotlightAction[] = [];

    // Filter actions based on query
    const filtered = navigationActions.filter(
      (action) =>
        action.label.toLowerCase().includes(query.toLowerCase()) ||
        action.description.toLowerCase().includes(query.toLowerCase())
    );

    items.push(...filtered);

    // Jump straight to a system hub — replaces the retired explorer tree's quick-nav
    if (query.trim() && canUploadRoms) {
      const lowerQuery = query.toLowerCase();
      const systemActions = (platforms ?? [])
        .filter(
          (platform) =>
            platform.name.toLowerCase().includes(lowerQuery) ||
            (platform.compactLabel ?? platform.name).toLowerCase().includes(lowerQuery),
        )
        .slice(0, 5)
        .map((platform) => ({
          id: `system:${platform.key}`,
          label: platform.name,
          description: `Open the ${platform.compactLabel} system hub`,
          leftSection: <IconDeviceGamepad2 size={18} />,
          onClick: () => navigate(`/systems/${platform.key}`),
        }));
      items.push(...systemActions);
    }

    // Add search action if query is not empty
    if (query.trim()) {
      items.push({
        id: `search:${query}`,
        label: `Search for "${query}"`,
        description: 'Search titles in catalog',
        leftSection: <IconSearch size={18} />,
        onClick: () => navigate(`/catalog?q=${encodeURIComponent(query)}`),
      });
    }

    return items;
  }, [query, navigationActions, navigate, platforms, canUploadRoms]);

  return (
    <Spotlight
      actions={actions}
      query={query}
      onQueryChange={setQuery}
      shortcut={['mod + K', 'mod + P']}
      nothingFound="No results found"
      highlightQuery
      searchProps={{
        leftSection: <IconSearch size={18} style={{ color: 'var(--mantine-color-dimmed)' }} />,
        placeholder: 'Search or jump to...',
      }}
      styles={{
        content: {
          backgroundColor: 'var(--mantine-color-body)',
          border: '1px solid var(--mantine-color-default-border)',
        },
        search: {
          backgroundColor: 'transparent',
          borderBottom: '1px solid var(--mantine-color-default-border)',
        },
        action: {
          borderRadius: 'var(--mantine-radius-sm)',
          '&[data-selected]': {
            backgroundColor: 'var(--mantine-primary-color-light)',
          },
        },
      }}
    />
  );
}

export { spotlight };
