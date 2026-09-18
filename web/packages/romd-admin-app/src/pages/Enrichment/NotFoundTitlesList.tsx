import {
  ActionIcon,
  Badge,
  Button,
  Card,
  Collapse,
  Group,
  Select,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconCheck, IconLink, IconSearch, IconX } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { useAssociateExternalId } from '../../hooks/api/useTitleActions';
import { enrichmentTitlesKeys, useTitlesByEnrichmentStatus } from './useTitlesByEnrichmentStatus';

interface TitleListItemProps {
  title: CatalogTitle;
}

function TitleListItem({ title }: TitleListItemProps) {
  const [showLinker, setShowLinker] = useState(false);
  const [source, setSource] = useState<string>('IGDB');
  const [externalId, setExternalId] = useState('');
  const associateId = useAssociateExternalId();
  const queryClient = useQueryClient();

  const handleLink = async () => {
    if (!externalId.trim()) return;

    try {
      await associateId.mutateAsync({
        titleId: title.id,
        source,
        externalId: externalId.trim(),
      });
      notifications.show({
        title: 'External ID linked',
        message: `Successfully linked ${source} ID: ${externalId} to "${title.name}"`,
        color: 'green',
      });
      setExternalId('');
      setShowLinker(false);
      queryClient.invalidateQueries({ queryKey: enrichmentTitlesKeys.all });
    } catch {
      notifications.show({
        title: 'Link failed',
        message: 'Could not associate the external ID.',
        color: 'red',
      });
    }
  };

  return (
    <Card withBorder padding="sm">
      <Group justify="space-between" wrap="nowrap">
        <Text
          component={Link}
          to={`/titles/${title.id}`}
          size="sm"
          fw={500}
          truncate
          style={{ minWidth: 0, flex: 1, textDecoration: 'none', color: 'inherit' }}
        >
          {title.name}
        </Text>
        <Group gap="xs">
          <Badge variant="light" color="orange" leftSection={<IconSearch size={12} />}>
            Not Found
          </Badge>
          <Tooltip label={showLinker ? 'Cancel' : 'Link external ID'}>
            <ActionIcon
              variant="light"
              color={showLinker ? 'gray' : 'blue'}
              size="sm"
              onClick={() => setShowLinker(!showLinker)}
            >
              {showLinker ? <IconX size={14} /> : <IconLink size={14} />}
            </ActionIcon>
          </Tooltip>
        </Group>
      </Group>
      <Collapse in={showLinker}>
        <Group gap="xs" mt="sm" align="flex-end">
          <Select
            size="xs"
            data={[{ value: 'IGDB', label: 'IGDB' }]}
            value={source}
            onChange={(v) => v && setSource(v)}
            w={100}
          />
          <TextInput
            size="xs"
            placeholder="External ID (e.g., 12345)"
            value={externalId}
            onChange={(e) => setExternalId(e.target.value)}
            style={{ flex: 1 }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') handleLink();
            }}
          />
          <Button
            size="xs"
            leftSection={<IconCheck size={14} />}
            onClick={handleLink}
            loading={associateId.isPending}
            disabled={!externalId.trim()}
          >
            Link
          </Button>
        </Group>
      </Collapse>
    </Card>
  );
}

function LoadingState() {
  return (
    <Stack gap="sm">
      {[1, 2, 3, 4, 5].map((i) => (
        <Skeleton key={i} height={60} />
      ))}
    </Stack>
  );
}

/**
 * Shows titles that were not found in IGDB and need manual linking.
 * Includes inline external ID linking per row.
 */
export function NotFoundTitlesList() {
  const { data: titles, isLoading } = useTitlesByEnrichmentStatus('NotFound');

  if (isLoading) {
    return <LoadingState />;
  }

  if (!titles || titles.length === 0) {
    return (
      <Text size="sm" c="dimmed" ta="center" py="xl">
        No titles with "not found" status.
      </Text>
    );
  }

  return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed" mb="xs">
        These titles were not found in IGDB. Use the link button to manually associate an external
        ID, or click the title name to view details.
      </Text>
      {titles.map((title) => (
        <TitleListItem key={title.id} title={title} />
      ))}
    </Stack>
  );
}
