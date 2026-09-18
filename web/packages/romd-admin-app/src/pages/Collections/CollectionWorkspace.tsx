import { Alert, Badge, Button, Group, Modal, Stack, Tabs, Text, Title } from '@mantine/core';
import { IconArrowLeft } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { CurationHelp } from '../../components/Workspace/CurationHelp';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCollection, useDeleteCollection } from '../../hooks/api/useCollectionManagement';
import { usePermissions } from '../../hooks/usePermissions';
import { CollectionAudiences, useCollectionPlacements } from './CollectionAudiences';
import { CollectionBuild } from './CollectionBuild';
import { CollectionFormModal } from './CollectionFormModal';
import { CollectionPreview } from './CollectionPreview';
import classes from './Collections.module.css';

export function CollectionWorkspace() {
  const { collectionId = '' } = useParams();
  const permissions = usePermissions();
  const collection = useCollection(collectionId);
  const placements = useCollectionPlacements(collectionId, permissions.canManageUsers);
  const remove = useDeleteCollection();
  const [params, setParams] = useSearchParams();
  const tab =
    permissions.canManageUsers &&
    [
      'audiences',
      'preview',
    ].includes(params.get('tab') ?? '')
      ? (params.get('tab') ?? 'build')
      : 'build';
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [busy, setBusy] = useState(false);
  const navigate = useNavigate();
  useEffect(() => {
    setEditing(false);
    setDeleting(false);
    setBusy(false);
  }, [
    collectionId,
  ]);
  if (collection.isError)
    return (
      <div className={classes.page}>
        <Alert color="red">
          Could not load this collection. It may have been deleted.{' '}
          <Button
            variant="subtle"
            onClick={() => collection.refetch()}
          >
            Retry
          </Button>
        </Alert>
        <Button
          component={Link}
          to="/collections"
          variant="subtle"
          mt="md"
        >
          Back to collections
        </Button>
      </div>
    );
  if (!collection.data)
    return (
      <div className={classes.page}>
        <Text>Loading collection…</Text>
      </div>
    );
  const detail = collection.data;
  const canEdit = permissions.canUploadRoms && !detail.isSystem;
  const canDelete = permissions.canManageTitles && !detail.isSystem;
  const audienceNames = placements.data?.map((p) => p.name).join(', ');
  return (
    <div className={classes.page}>
      <Button
        component={Link}
        to="/collections"
        variant="subtle"
        color="gray"
        p={0}
        leftSection={<IconArrowLeft size={15} />}
        disabled={busy}
      >
        All collections
      </Button>
      <div className={classes.header}>
        <Group
          align="flex-start"
          justify="space-between"
        >
          <Group
            align="flex-start"
            wrap="nowrap"
          >
            {detail.coverUrl && (
              <img
                src={detail.coverUrl}
                alt=""
                className={classes.heroArt}
              />
            )}
            <div style={{ minWidth: 0 }}>
              <Group gap="xs">
              <Title
                className={classes.heading}
                order={1}
              >
                {detail.name}
              </Title>
              <CurationHelp concept="Collections" />
              </Group>
              {detail.description && <Text
                className={classes.intro}
                mt="sm"
              >
                {detail.description}
              </Text>}
              <Group
                gap="sm"
                mt="md"
              >
                <Text size="sm" c="dimmed">{detail.items.length} {detail.items.length === 1 ? 'title' : 'titles'}</Text>
                {detail.isSystem && <Badge color="gray">System managed</Badge>}
              </Group>
            </div>
          </Group>
          <Group>
            {canEdit && (
              <Button
                variant="light"
                {...workspaceActionProps}
                disabled={busy}
                onClick={() => setEditing(true)}
              >
                Edit details & artwork
              </Button>
            )}
            {canDelete && (
              <Button
                variant="subtle"
                color="red"
                disabled={busy}
                onClick={() => {
                  remove.reset();
                  setDeleting(true);
                }}
              >
                Delete collection
              </Button>
            )}
          </Group>
        </Group>
      </div>
      {permissions.canManageUsers && <Group className={classes.shared} gap="sm">
        <Text size="sm" c="dimmed">Libraries</Text>
        {placements.isError ? <Button variant="subtle" size="compact-xs" onClick={() => placements.refetch()}>Retry library associations</Button>
          : placements.isPending ? <Text size="sm">Loading libraries...</Text>
            : placements.data?.length ? placements.data.map((p) => <Text component={Link} key={p.libraryId} to={`/libraries/${p.libraryId}?tab=collections`} size="sm" className={classes.relationshipLink}>{p.name}</Text>)
              : <Text component={Link} to={`/collections/${collectionId}?tab=audiences`} size="sm" className={classes.relationshipLink}>Not used in a library</Text>}
      </Group>}
      <Tabs
        value={tab}
        onChange={(value) => {
          if (value && !busy) {
            const next = new URLSearchParams(params);
            next.set('tab', value);
            setParams(next);
          }
        }}
        color="teal"
        mt="lg"
        className={classes.tabs}
        classNames={{
          list: classes.tabsList,
          tab: classes.tab,
        }}
      >
        <Tabs.List>
          <Tabs.Tab
            value="build"
            disabled={busy}
          >
            Build
          </Tabs.Tab>
          {permissions.canManageUsers && (
            <>
              <Tabs.Tab
                value="audiences"
                disabled={busy}
              >
                Audiences
              </Tabs.Tab>
              <Tabs.Tab
                value="preview"
                disabled={busy}
              >
                Preview
              </Tabs.Tab>
            </>
          )}
        </Tabs.List>
      </Tabs>
      {tab === 'build' && (
        <CollectionBuild
          key={collectionId}
          collection={detail}
          canEdit={canEdit}
          onBusyChange={setBusy}
        />
      )}
      {tab === 'audiences' && (
        <div className={classes.panel}>
          <CollectionAudiences collectionId={collectionId} />
        </div>
      )}
      {tab === 'preview' && (
        <CollectionPreview
          key={collectionId}
          collection={detail}
        />
      )}
      <CollectionFormModal
        opened={editing}
        onClose={() => setEditing(false)}
        collection={detail}
        coverChoices={detail.items}
        affectedAudiences={audienceNames}
      />
      <Modal
        opened={deleting}
        onClose={() => {
          if (!remove.isPending) setDeleting(false);
        }}
        title={`Delete ${detail.name}?`}
        centered
      >
        <Stack>
          <Text>
            This removes the collection from every attached library. Games and their files are
            retained.
          </Text>
          {audienceNames && (
            <Text
              size="sm"
              c="dimmed"
            >
              Affected audiences: {audienceNames}
            </Text>
          )}
          {remove.isError && <Alert color="red">{remove.error.message}</Alert>}
          <Group justify="flex-end">
            <Button
              variant="subtle"
              disabled={remove.isPending}
              onClick={() => setDeleting(false)}
            >
              Cancel
            </Button>
            <Button
              color="red"
              loading={remove.isPending}
              onClick={async () => {
                try {
                  await remove.mutateAsync(collectionId);
                  navigate('/collections');
                } catch {
                  /* Display mutation error. */
                }
              }}
            >
              Delete permanently
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
