import { Alert, Badge, Button, Group, Skeleton, Stack, Text, TextInput, Title } from '@mantine/core';
import { IconPlus, IconSearch, IconStack2 } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router';
import { CurationHelp } from '../../components/Workspace/CurationHelp';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCollections } from '../../hooks/api/useCollectionManagement';
import { useLibraries } from '../../hooks/api/useLibraryManagement';
import { usePermissions } from '../../hooks/usePermissions';
import { CollectionFormModal } from './CollectionFormModal';
import classes from './Collections.module.css';

export function Collections() {
  const { canUploadRoms, canManageUsers } = usePermissions();
  const query = useCollections();
  const libraries = useLibraries(canManageUsers);
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [opened, setOpened] = useState(false);
  if (params.get('collection')) return <Navigate replace to={`/collections/${params.get('collection')}`} />;
  const filtered = (query.data ?? []).filter((c) => `${c.name} ${c.description ?? ''}`.toLowerCase().includes(search.trim().toLowerCase()));
  const associationsAvailable = libraries.isSuccess && libraries.data.every((l) => l.collectionIds != null);
  return (
    <div className={classes.page}>
      <Group className={classes.header} justify="space-between">
        <Group gap="xs"><Title order={1} className={classes.heading}>Collections</Title><CurationHelp concept="Collections" /></Group>
        {canUploadRoms && <Button {...workspaceActionProps} leftSection={<IconPlus size={17} />} onClick={() => setOpened(true)}>New collection</Button>}
      </Group>
      <Group justify="space-between" mb="md">
        <Text size="sm" c="dimmed">{query.isSuccess ? `${filtered.length} ${filtered.length === 1 ? 'collection' : 'collections'}` : 'Collections'}</Text>
        <TextInput aria-label="Find a collection" placeholder="Find a collection" value={search} onChange={(e) => setSearch(e.currentTarget.value)} leftSection={<IconSearch size={16} />} />
      </Group>
      {query.isError ? <Alert color="red">Could not load collections. <Button variant="subtle" onClick={() => query.refetch()}>Retry</Button></Alert>
        : query.isPending ? <Stack>{[1, 2, 3].map((id) => <Skeleton key={id} h={80} />)}</Stack>
        : filtered.length === 0 ? <Stack py="xl" align="flex-start">
          <Title order={2} className={classes.sectionHeading}>{search ? 'No matching collections' : 'No collections yet'}</Title>
          {search && <Button variant="subtle" onClick={() => setSearch('')}>Clear search</Button>}
        </Stack> : <ul className={classes.list}>
          {filtered.map((collection) => {
            const placements = (libraries.data ?? []).filter((l) => l.collectionIds?.includes(collection.id));
            const count = Number(collection.itemCount ?? 0);
            return <li key={collection.id} className={classes.listRow}>
              <Link to={`/collections/${collection.id}`} className={classes.collectionLink}>
                {collection.coverUrl ? <img src={collection.coverUrl} alt="" className={classes.art} /> : <span className={classes.art}><IconStack2 size={22} /></span>}
                <div className={classes.rowContent}>
                  <Group gap="xs"><Text fw={600} size="sm">{collection.name}</Text>{collection.isSystem && <Badge color="gray" size="xs">System</Badge>}</Group>
                  {collection.description && <Text size="sm" c="dimmed" lineClamp={2}>{collection.description}</Text>}
                  <Text size="xs" c="dimmed">{count} {count === 1 ? 'title' : 'titles'}{!collection.coverUrl ? ' · No artwork' : ''}</Text>
                </div>
              </Link>
              {canManageUsers && <div className={classes.placements}>
                {libraries.isError ? <Button variant="subtle" size="compact-xs" onClick={() => libraries.refetch()}>Retry library associations</Button>
                  : !associationsAvailable ? <Text size="xs" c="dimmed">{libraries.isPending ? 'Loading libraries...' : 'Library associations unavailable'}</Text>
                  : placements.length ? <Stack gap={4}><Text size="xs" c="dimmed">Libraries</Text><Group gap="xs">{placements.map((library) => <Text key={library.id} component={Link} to={`/libraries/${library.id}?tab=collections`} size="xs" className={classes.relationshipLink}>{library.name}</Text>)}</Group></Stack>
                    : <Text component={Link} to={`/collections/${collection.id}?tab=audiences`} size="xs" className={classes.relationshipLink}>Not used in a library</Text>}
              </div>}
            </li>;
          })}
        </ul>}
      <CollectionFormModal opened={opened} onClose={() => setOpened(false)} collection={null} onSaved={(id) => navigate(`/collections/${id}?tab=build`)} />
    </div>
  );
}
